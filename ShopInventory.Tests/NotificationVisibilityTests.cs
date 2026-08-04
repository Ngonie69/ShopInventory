using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Notifications;
using ShopInventory.Hubs;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A notification is only shown when its category matches one of the
/// <c>*BroadcastCategories</c> lists the viewer's roles can see — see
/// <c>NotificationService.BuildVisibleNotificationsQuery</c>. A producer that invents a category
/// nobody listed therefore writes rows that are stored, counted against nothing, and displayed to
/// no one, with no error anywhere.
///
/// That is not hypothetical: "ProductCatalog" was written for Merchandisers for its whole life and
/// was in none of the lists, so no Merchandiser ever saw a catalogue change, and no Admin did
/// either — the admin branch matches a role-targeted row only when the admin holds that role.
/// </summary>
public sealed class NotificationVisibilityTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly NotificationService _service;

    public NotificationVisibilityTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(options);
        _context.Database.EnsureCreated();

        _service = new NotificationService(
            _context,
            NullLogger<NotificationService>.Instance,
            new SilentHubContext(),
            new SilentPushNotificationService());
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// Every category any producer in the API writes, so a new producer that invents an unlisted
    /// category fails here rather than silently going unread in production. Add the category to
    /// this list and to a <c>*BroadcastCategories</c> list together.
    /// </summary>
    public static TheoryData<string> ProducedCategories() =>
    [
        "AppVersion",
        "BatchStatus",
        "CreditNote",
        "Customer",
        "GoodsReceiptPurchaseOrder",
        "IncomingPayment",
        "InventoryTransfer",
        "Invoice",
        "LowStock",
        "POD",
        "ProductCatalog",
        "PurchaseInvoice",
        "PurchaseOrder",
        "PurchaseQuotation",
        "PurchaseRequest",
        "Quotation",
        "SalesOrder",
        "Security",
        "System",
        "TransferApproval",
        "TransferRequest"
    ];

    [Theory]
    [MemberData(nameof(ProducedCategories))]
    public void EveryProducedCategoryBelongsToABroadcastList(string category)
    {
        var allListedCategories = typeof(NotificationAudienceRules)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.Name.EndsWith("BroadcastCategories", StringComparison.Ordinal))
            .Select(field => (string[])field.GetValue(null)!)
            .SelectMany(categories => categories)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.True(
            allListedCategories.Contains(category),
            $"Category \"{category}\" is written by a producer but appears in no *BroadcastCategories " +
            "list, so BuildVisibleNotificationsQuery will hide it from every non-admin and from any " +
            "admin who does not hold its target role. Add it to the list whose audience should see it.");
    }

    [Fact]
    public async Task ProductCatalogNotificationReachesAMerchandiser()
    {
        await _service.CreateNotificationAsync(new CreateNotificationRequest
        {
            Title = "Product catalog updated",
            Message = "Item ABC123 was activated.",
            Type = "Info",
            Category = "ProductCatalog",
            EntityType = "ProductCatalog",
            EntityId = "ABC123",
            TargetRole = "Merchandiser"
        });

        var visible = await _service.GetNotificationsAsync("mmoyo", ["Merchandiser"]);

        Assert.Equal(1, visible.TotalCount);
        Assert.Equal("Product catalog updated", visible.Notifications.Single().Title);
    }

    /// <summary>
    /// The low-stock alert is a broadcast, not an admin-targeted row, so stock control — who acts on
    /// it — receives it. Its audience is the intersection of the two filters the query applies: the
    /// Inventory audience for the category, and the Catalogue audience for the /products route.
    /// </summary>
    [Fact]
    public async Task LowStockAlertReachesStockControlAndNotSales()
    {
        await _service.CreateLowStockAlertAsync("ABC123", "Cheddar 1kg", currentStock: 2m, reorderLevel: 10m);

        var stockController = await _service.GetNotificationsAsync("tchuma", ["StockController"]);
        var salesRep = await _service.GetNotificationsAsync("nmoyo", ["SalesRep"]);

        Assert.Equal(1, stockController.TotalCount);
        Assert.Equal(0, salesRep.TotalCount);
    }

    [Fact]
    public async Task LowStockAlertIsNotRepeatedForTheSameItem()
    {
        await _service.CreateLowStockAlertAsync("ABC123", "Cheddar 1kg", currentStock: 2m, reorderLevel: 10m);
        await _service.CreateLowStockAlertAsync("ABC123", "Cheddar 1kg", currentStock: 1m, reorderLevel: 10m);

        var raised = await _context.Notifications
            .AsNoTracking()
            .CountAsync(notification => notification.EntityId == "ABC123" && notification.Category == "LowStock");

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task LowStockAlertsForDifferentItemsAreBothRaised()
    {
        await _service.CreateLowStockAlertAsync("ABC123", "Cheddar 1kg", currentStock: 2m, reorderLevel: 10m);
        await _service.CreateLowStockAlertAsync("XYZ789", "Gouda 500g", currentStock: 3m, reorderLevel: 10m);

        var stockController = await _service.GetNotificationsAsync("tchuma", ["StockController"]);

        Assert.Equal(2, stockController.TotalCount);
    }

    private sealed class SilentHubContext : IHubContext<NotificationHub>
    {
        public IHubClients Clients { get; } = new SilentClients();

        public IGroupManager Groups { get; } = new SilentGroupManager();
    }

    private sealed class SilentClients : IHubClients, IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public IClientProxy All => this;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => this;
        public IClientProxy Client(string connectionId) => this;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => this;
        public IClientProxy Group(string groupName) => this;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => this;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => this;
        public IClientProxy User(string userId) => this;
        public IClientProxy Users(IReadOnlyList<string> userIds) => this;
    }

    private sealed class SilentGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class SilentPushNotificationService : IPushNotificationService
    {
        public Task<DeviceRegistrationDto> RegisterDeviceAsync(Guid userId, RegisterDeviceRequest request, CancellationToken ct = default)
            => Task.FromResult(new DeviceRegistrationDto());

        public Task UnregisterDeviceAsync(Guid userId, string deviceToken, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<List<DeviceRegistrationDto>> GetUserDevicesAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult(new List<DeviceRegistrationDto>());

        public Task<int> SendToUserAsync(Guid userId, string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> SendToUsernameAsync(string username, string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> SendToRoleAsync(string role, string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> SendToAllAsync(string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task CleanupStaleTokensAsync(CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
