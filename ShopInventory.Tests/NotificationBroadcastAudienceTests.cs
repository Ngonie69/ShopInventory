using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Notifications;
using ShopInventory.Hubs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A broadcast notification — no target user, no target role — is delivered twice over: once by
/// <c>GetBroadcastAudienceRoles</c>, which picks the SignalR groups and the roles whose devices get
/// a push, and once by <c>BuildVisibleNotificationsQuery</c>, which decides whose list it appears
/// in. Nothing made the two agree, and they didn't.
///
/// The audience resolver returned the action URL's audience whenever the route matched, ignoring
/// the category entirely. So the morning low-stock sweep — category "LowStock", pointing at
/// "/stock" — resolved to the Catalogue audience, and every merchandiser's phone rang at 07:30 with
/// "Low stock: 490580 item(s), 490100 critical". The list filter still applied both rules, so the
/// notification was nowhere to be found in the app afterwards: a push with no home.
///
/// These pin the two halves together. The consistency theory is the general statement; the
/// low-stock tests are the case that reached production.
/// </summary>
public sealed class NotificationBroadcastAudienceTests : IDisposable
{
    private const string NoParticularUser = "nobody";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly RecordingPushNotificationService _push = new();
    private readonly NotificationService _service;

    public NotificationBroadcastAudienceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();

        _service = new NotificationService(
            _context,
            NullLogger<NotificationService>.Instance,
            new SilentHubContext(),
            _push);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// The category and route combinations broadcast producers actually write. A pair belongs here
    /// once some producer writes it, so a new one that resolves an audience its readers cannot see
    /// fails here rather than in someone's notification tray.
    /// </summary>
    public static TheoryData<string, string?> BroadcastShapes() => new()
    {
        // The morning low-stock sweep: the summary, then the worst items individually.
        { "LowStock", "/stock" },
        { "LowStock", "/products?search=ABC123" },
        { "SalesOrder", "/sales-orders" },
        { "SalesOrder", "/mobile-drafts" },
        { "Customer", "/customers" },
        { "Invoice", "/invoices" },
        { "CreditNote", "/credit-notes" },
        { "IncomingPayment", "/payments" },
        { "InventoryTransfer", "/inventory-transfers" },
        { "TransferRequest", "/transfer-requests" },
        { "PurchaseOrder", "/purchase-orders" },
        { "GoodsReceiptPurchaseOrder", "/goods-receipt-pos" },
        { "POD", "/pods" },
        { "BatchStatus", "/lab/batch-status" },
        { "System", "/sync-status" },
        { "System", null },
        { "Security", null }
    };

    /// <summary>
    /// Whoever is sent a broadcast can find it afterwards, and whoever cannot find it is not sent
    /// it. Both directions matter: the first would leave a push that opens an empty list, the
    /// second is the leak — a notification on a phone belonging to a role the app hides it from.
    /// </summary>
    [Theory]
    [MemberData(nameof(BroadcastShapes))]
    public async Task TheRolesABroadcastIsSentToAreTheRolesThatCanSeeIt(string category, string? actionUrl)
    {
        await _service.CreateNotificationAsync(new CreateNotificationRequest
        {
            Title = $"{category} broadcast",
            Message = "Body",
            Type = "Info",
            Category = category,
            ActionUrl = actionUrl
        });

        var audience = NotificationAudienceRules.GetBroadcastAudienceRoles(category, actionUrl);
        var disagreements = new List<string>();

        foreach (var role in AllKnownRoles())
        {
            var sent = audience.Contains(role, StringComparer.OrdinalIgnoreCase);
            var shown = (await _service.GetNotificationsAsync(NoParticularUser, [role])).TotalCount > 0;

            if (sent != shown)
            {
                disagreements.Add(sent
                    ? $"{role} is sent the notification but never sees it in the list"
                    : $"{role} sees the notification in the list but is never sent it");
            }
        }

        Assert.True(
            disagreements.Count == 0,
            $"Category \"{category}\" with ActionUrl \"{actionUrl ?? "(none)"}\" resolves an audience " +
            $"that does not match who the notification list admits:{Environment.NewLine}" +
            string.Join(Environment.NewLine, disagreements));
    }

    /// <summary>
    /// The one that reached production. Stock control acts on a low-stock alert; a merchandiser out
    /// on a round cannot, and their app will not even show it to them.
    /// </summary>
    [Fact]
    public async Task TheMorningLowStockSummaryIsNotPushedToMerchandiserPhones()
    {
        await _service.CreateNotificationAsync(new CreateNotificationRequest
        {
            Title = "Low stock: 490580 item(s), 490100 critical",
            Message = "490580 item/warehouse line(s) are at or below a reorder level of 10.",
            Type = "Error",
            Category = "LowStock",
            ActionUrl = "/stock",
            EntityType = "LowStockReview"
        });

        Assert.Equal(
            new[] { ApplicationRoles.Admin, ApplicationRoles.StockController },
            _push.PushedRoles);
    }

    [Fact]
    public async Task ALowStockItemAlertIsNotPushedToMerchandiserPhones()
    {
        await _service.CreateLowStockAlertAsync("ABC123", "Cheddar 1kg", currentStock: 0m, reorderLevel: 10m);

        Assert.Equal(
            new[] { ApplicationRoles.Admin, ApplicationRoles.StockController },
            _push.PushedRoles);
    }

    /// <summary>
    /// A merchandiser is answerable for the orders they submitted, and that is the whole of what
    /// their app has to show them. Whatever a broadcast is about and wherever it points, it is not
    /// addressed to them.
    /// </summary>
    [Theory]
    [MemberData(nameof(BroadcastShapes))]
    public void AMerchandiserIsNeverInABroadcastAudience(string category, string? actionUrl)
    {
        Assert.DoesNotContain(
            ApplicationRoles.Merchandiser,
            NotificationAudienceRules.GetBroadcastAudienceRoles(category, actionUrl));
    }

    /// <summary>
    /// The other half of the same rule: what a merchandiser is sent and what their list shows have
    /// to be the same set, or a push arrives with nothing behind it.
    /// </summary>
    [Fact]
    public async Task AMerchandiserIsShownTheirOwnOrderAndNoBroadcast()
    {
        var merchandiser = await AddMerchandiserAsync();

        // Their own order, as SalesOrderLifecycleNotificationFactory addresses it.
        await _service.CreateNotificationAsync(new CreateNotificationRequest
        {
            Title = "Order SO-4471 posted",
            Message = "Your order for Kefalos Tuckshop was posted to SAP.",
            Type = "Success",
            Category = "SalesOrder",
            EntityType = "SalesOrder",
            EntityId = "SO-4471",
            ActionUrl = "/mobile-drafts",
            TargetUserId = merchandiser.Id,
            TargetUsername = merchandiser.Username
        });

        // A broadcast in a category and on a route a merchandiser is otherwise listed for.
        await _service.CreateNotificationAsync(new CreateNotificationRequest
        {
            Title = "New customer added",
            Message = "A customer was created in SAP.",
            Type = "Info",
            Category = "Customer",
            ActionUrl = "/customers"
        });

        var inbox = await _service.GetNotificationsAsync(
            merchandiser.Username, [ApplicationRoles.Merchandiser]);

        Assert.Equal("Order SO-4471 posted", Assert.Single(inbox.Notifications).Title);
        Assert.Equal(new[] { merchandiser.Id }, _push.PushedUserIds);
        Assert.DoesNotContain(ApplicationRoles.Merchandiser, _push.PushedRoles);
    }

    /// <summary>
    /// An audience is never allowed to come back empty: <c>CreateNotificationAsync</c> reads an
    /// empty one as "this is for everybody" and pushes to every registered device there is.
    /// </summary>
    [Theory]
    [InlineData("LowStock", "/pods")]
    [InlineData("Nonexistent", "/stock")]
    [InlineData("Nonexistent", null)]
    [InlineData(null, "/nowhere-in-particular")]
    public void AnAudienceThatNarrowsToNothingIsStillAdminOnly(string? category, string? actionUrl)
    {
        Assert.Equal(
            new[] { ApplicationRoles.Admin },
            NotificationAudienceRules.GetBroadcastAudienceRoles(category, actionUrl));
    }

    private async Task<User> AddMerchandiserAsync()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "mmoyo",
            Email = "mmoyo@example.test",
            PasswordHash = "x",
            Role = ApplicationRoles.Merchandiser,
            IsActive = true
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }

    /// <summary>
    /// Every role the audience rules name, gathered from the rules themselves so a role added to a
    /// list is checked without anyone remembering to add it here too.
    /// </summary>
    private static string[] AllKnownRoles() =>
        typeof(NotificationAudienceRules)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.Name.EndsWith("AudienceRoles", StringComparison.Ordinal))
            .SelectMany(field => (string[])field.GetValue(null)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private sealed class RecordingPushNotificationService : IPushNotificationService
    {
        private readonly List<string> _pushedRoles = [];
        private readonly List<Guid> _pushedUserIds = [];

        /// <summary>Roles whose devices were sent a push, in the order they were sent.</summary>
        public IReadOnlyList<string> PushedRoles => _pushedRoles;

        /// <summary>Users pushed to individually.</summary>
        public IReadOnlyList<Guid> PushedUserIds => _pushedUserIds;

        public Task<int> SendToRoleAsync(string role, string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default)
        {
            _pushedRoles.Add(role);
            return Task.FromResult(0);
        }

        public Task<int> SendToUserAsync(Guid userId, string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default)
        {
            _pushedUserIds.Add(userId);
            return Task.FromResult(0);
        }

        public Task<int> SendToUsernameAsync(string username, string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default) =>
            throw new InvalidOperationException($"Push fell back to a username lookup for \"{username}\" — the user id should have resolved.");

        // Every registered device in the company, which is never what a notification with an
        // audience meant.
        public Task<int> SendToAllAsync(string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("A notification was pushed to every registered device.");

        // Silent data pushes do not come from the notification service at all.
        public Task<int> SendSilentDataToRoleAsync(string role, Dictionary<string, string> data, CancellationToken ct = default) =>
            throw new InvalidOperationException($"The notification service sent a silent data push to {role}.");

        public Task<DeviceRegistrationDto> RegisterDeviceAsync(Guid userId, RegisterDeviceRequest request, CancellationToken ct = default) =>
            Task.FromResult(new DeviceRegistrationDto());

        public Task UnregisterDeviceAsync(Guid userId, string deviceToken, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<List<DeviceRegistrationDto>> GetUserDevicesAsync(Guid userId, CancellationToken ct = default) =>
            Task.FromResult(new List<DeviceRegistrationDto>());

        public Task<int> SendToDeviceTokensAsync(
            IReadOnlyCollection<string> deviceTokens,
            string title,
            string body,
            Dictionary<string, string>? data = null,
            CancellationToken ct = default)
            => Task.FromResult(0);

        public Task CleanupStaleTokensAsync(CancellationToken ct = default) => Task.CompletedTask;
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
}
