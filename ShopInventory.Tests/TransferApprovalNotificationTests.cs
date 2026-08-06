using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Hubs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// An authorizer only learns a transfer is waiting on them from the notification bell, so the
/// approval engine writing the row is only half of it — the row also has to come back out of the
/// feed the bell reads. Everything else covering the approval engine stubs the notification
/// service out, which leaves that whole seam untested.
/// </summary>
public sealed class TransferApprovalNotificationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly NotificationService _notifications;

    public TransferApprovalNotificationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();

        _notifications = new NotificationService(
            _context,
            NullLogger<NotificationService>.Instance,
            new NoOpHubContext(),
            new StubPushNotificationService());
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_new_transfer_request_notifies_its_authorizer()
    {
        var requester = await AddUserAsync(ApplicationRoles.StockController);
        var authorizer = await AddUserAsync(ApplicationRoles.DepotController, "WH02");

        await ApprovalService().EnsureRequestAsync(TransferRequest(5001), requester.Id, default);

        var written = Assert.Single(await _context.Notifications.ToListAsync());
        Assert.Equal("TransferApproval", written.Category);
        Assert.Equal(authorizer.Username, written.TargetUsername);
    }

    [Fact]
    public async Task The_authorizers_bell_shows_the_approval()
    {
        var requester = await AddUserAsync(ApplicationRoles.StockController);
        var authorizer = await AddUserAsync(ApplicationRoles.DepotController, "WH02");

        await ApprovalService().EnsureRequestAsync(TransferRequest(5002), requester.Id, default);

        var feed = await _notifications.GetNotificationsAsync(
            authorizer.Username, [authorizer.Role], 1, 5, false, null, default);

        Assert.Equal(1, feed.UnreadCount);
        Assert.Single(feed.Notifications, item => item.Category == "TransferApproval");
    }

    [Fact]
    public async Task An_administrator_authorizer_sees_the_approval_too()
    {
        // The catch-all template routes to the administrator stage, and administrators read the
        // feed through a different branch of the visibility query than every other role.
        var requester = await AddUserAsync(ApplicationRoles.Cashier);
        var admin = await AddUserAsync(ApplicationRoles.Admin);

        await ApprovalService().EnsureRequestAsync(TransferRequest(5003), requester.Id, default);

        var feed = await _notifications.GetNotificationsAsync(
            admin.Username, [admin.Role], 1, 5, false, null, default);

        Assert.Equal(1, feed.UnreadCount);
        Assert.Single(feed.Notifications, item => item.Category == "TransferApproval");
    }

    [Fact]
    public async Task The_approval_links_to_the_document_it_is_about()
    {
        // The link used to be truncated at the '?', which left the authorizer on an unfiltered list
        // of every transfer request with no indication which one wanted them.
        var requester = await AddUserAsync(ApplicationRoles.StockController);
        await AddUserAsync(ApplicationRoles.DepotController, "WH02");

        await ApprovalService().EnsureRequestAsync(TransferRequest(5004), requester.Id, default);

        var written = Assert.Single(await _context.Notifications.ToListAsync());
        Assert.Equal("/inventory-transfers?requestDocEntry=5004", written.ActionUrl);
    }

    // ── What the requester hears back ───────────────────

    [Fact]
    public async Task Approving_a_request_tells_the_person_who_raised_it()
    {
        var requester = await AddUserAsync(ApplicationRoles.StockController);
        var authorizer = await AddUserAsync(ApplicationRoles.DepotController, "WH02");
        var document = TransferRequest(5005);
        await ApprovalService().EnsureRequestAsync(document, requester.Id, default);

        await ApprovalService().SubmitDecisionAsync(
            document, authorizer.Id, ApprovalDecisionValues.Approved, null, "Stock confirmed", default);

        var feed = await _notifications.GetNotificationsAsync(
            requester.Username, [requester.Role], 1, 10, false, null, default);
        var outcome = Assert.Single(feed.Notifications, item => item.EntityType == "TransferApprovalOutcome");
        Assert.Equal("Transfer request #5005 approved", outcome.Title);
        Assert.Contains("Stock confirmed", outcome.Message);
        Assert.False(outcome.IsRead);
    }

    [Fact]
    public async Task Rejecting_a_request_tells_the_person_who_raised_it_why()
    {
        var requester = await AddUserAsync(ApplicationRoles.StockController);
        var authorizer = await AddUserAsync(ApplicationRoles.DepotController, "WH02");
        var document = TransferRequest(5006);
        await ApprovalService().EnsureRequestAsync(document, requester.Id, default);

        await ApprovalService().SubmitDecisionAsync(
            document, authorizer.Id, ApprovalDecisionValues.NotApproved, null, "No stock at WH01", default);

        var feed = await _notifications.GetNotificationsAsync(
            requester.Username, [requester.Role], 1, 10, false, null, default);
        var outcome = Assert.Single(feed.Notifications, item => item.EntityType == "TransferApprovalOutcome");
        Assert.Equal("Transfer request #5006 was not approved", outcome.Title);
        Assert.Contains("No stock at WH01", outcome.Message);
        Assert.Equal("Warning", outcome.Type);
    }

    [Fact]
    public async Task Posting_the_transfer_tells_the_person_who_raised_the_request()
    {
        var requester = await AddUserAsync(ApplicationRoles.StockController);
        var authorizer = await AddUserAsync(ApplicationRoles.DepotController, "WH02");
        await ApprovalService().EnsureRequestAsync(TransferRequest(5007), requester.Id, default);

        await ApprovalService().MarkGeneratedAsync(5007, 9001, 9002, authorizer.Id, true, default);

        var feed = await _notifications.GetNotificationsAsync(
            requester.Username, [requester.Role], 1, 10, false, null, default);
        var outcome = Assert.Single(feed.Notifications, item => item.EntityType == "TransferApprovalOutcome");
        Assert.Equal("Transfer request #5007 posted as transfer #9002", outcome.Title);
    }

    [Fact]
    public async Task Posting_the_transfer_stops_the_authorizer_being_asked_to_approve_it()
    {
        // The document exists; there is no decision left to take. These alerts used to sit unread
        // on the authorizer's bell forever.
        var requester = await AddUserAsync(ApplicationRoles.StockController);
        var authorizer = await AddUserAsync(ApplicationRoles.DepotController, "WH02");
        await ApprovalService().EnsureRequestAsync(TransferRequest(5008), requester.Id, default);

        await ApprovalService().MarkGeneratedAsync(5008, 9003, 9004, requester.Id, false, default);

        var feed = await _notifications.GetNotificationsAsync(
            authorizer.Username, [authorizer.Role], 1, 10, false, null, default);
        Assert.Equal(0, feed.UnreadCount);
    }

    [Fact]
    public async Task Deciding_your_own_document_does_not_notify_you_about_it()
    {
        // An administrator authorizes their own request routinely. Telling them what they just did
        // is noise, and noise is what stops a bell being read.
        var admin = await AddUserAsync(ApplicationRoles.Admin);
        var document = TransferRequest(5009);
        await ApprovalService().EnsureRequestAsync(document, admin.Id, default);

        await ApprovalService().SubmitDecisionAsync(
            document, admin.Id, ApprovalDecisionValues.Approved, null, null, default);

        Assert.Empty(await _context.Notifications
            .Where(item => item.EntityType == "TransferApprovalOutcome")
            .ToListAsync());
    }

    [Fact]
    public async Task Approving_and_converting_in_one_go_leaves_the_requester_one_thing_to_read()
    {
        // Converting a request runs both steps back to back, so "approved" and "posted" arrive
        // together. Both are recorded; only the fuller one is still asking to be read.
        var requester = await AddUserAsync(ApplicationRoles.StockController);
        var authorizer = await AddUserAsync(ApplicationRoles.DepotController, "WH02");
        var document = TransferRequest(5011);
        await ApprovalService().EnsureRequestAsync(document, requester.Id, default);

        await ApprovalService().SubmitDecisionAsync(
            document, authorizer.Id, ApprovalDecisionValues.Approved, null, null, default);
        await ApprovalService().MarkGeneratedAsync(5011, 9007, 9008, authorizer.Id, true, default);

        var feed = await _notifications.GetNotificationsAsync(
            requester.Username, [requester.Role], 1, 10, false, null, default);
        Assert.Equal(1, feed.UnreadCount);
        var unread = Assert.Single(feed.Notifications, item => !item.IsRead);
        Assert.Equal("Transfer request #5011 posted as transfer #9008", unread.Title);
    }

    [Fact]
    public async Task An_outcome_is_only_reported_once()
    {
        var requester = await AddUserAsync(ApplicationRoles.StockController);
        var authorizer = await AddUserAsync(ApplicationRoles.DepotController, "WH02");
        await ApprovalService().EnsureRequestAsync(TransferRequest(5010), requester.Id, default);

        await ApprovalService().MarkGeneratedAsync(5010, 9005, 9006, authorizer.Id, true, default);
        await ApprovalService().MarkGeneratedAsync(5010, 9005, 9006, authorizer.Id, true, default);

        Assert.Single(await _context.Notifications
            .Where(item => item.EntityType == "TransferApprovalOutcome")
            .ToListAsync());
    }

    // ── Helpers ─────────────────────────────────────────

    private IInventoryTransferApprovalService ApprovalService() => new InventoryTransferApprovalService(
        _context,
        _notifications,
        NullLogger<InventoryTransferApprovalService>.Instance);

    private static InventoryTransferRequest TransferRequest(int docEntry) => new()
    {
        DocEntry = docEntry,
        DocNum = docEntry,
        DocumentStatus = "bost_Open",
        FromWarehouse = "WH01",
        ToWarehouse = "WH02"
    };

    private async Task<User> AddUserAsync(string role, params string[] warehouseCodes)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"{role.ToLowerInvariant()}-{suffix}",
            Email = $"{role.ToLowerInvariant()}-{suffix}@example.test",
            PasswordHash = "x",
            Role = role,
            IsActive = true
        };
        user.SetWarehouseCodes(warehouseCodes.ToList());
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }

    private sealed class NoOpHubContext : IHubContext<NotificationHub>
    {
        public IHubClients Clients { get; } = new NoOpClients();
        public IGroupManager Groups { get; } = new NoOpGroupManager();
    }

    private sealed class NoOpClients : IHubClients, IClientProxy
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

    private sealed class NoOpGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class StubPushNotificationService : IPushNotificationService
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

        public Task<int> SendSilentDataToRoleAsync(string role, Dictionary<string, string> data, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task CleanupStaleTokensAsync(CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
