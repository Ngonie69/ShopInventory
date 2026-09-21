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
/// A notification carrying <see cref="CreateNotificationRequest.WebhookEvent"/> publishes that event;
/// one without it publishes nothing.
/// </summary>
/// <remarks>
/// This is the seam most of the webhook vocabulary is published through. Almost every module that
/// would raise an event already raises a notification, so one opt-in field lights a site up without
/// a publish call of its own.
///
/// The opt-in is a field rather than a mapping off <c>Category</c> because Category is a free string
/// that has already drifted (SAP / SAP Posting / Synchronization / Sync Retry name one thing; Stock
/// and LowStock another). A mapping off it would fire the wrong event and would change meaning
/// whenever someone renamed a category.
/// </remarks>
public sealed class NotificationWebhookEventTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly RecordingWebhookService _webhooks = new();
    private readonly NotificationService _service;

    public NotificationWebhookEventTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _service = new NotificationService(
            _context,
            NullLogger<NotificationService>.Instance,
            new SilentHubContext(),
            new SilentPushService(),
            _webhooks);
    }

    [Fact]
    public async Task A_notification_that_names_an_event_publishes_it_with_the_entity_it_is_about()
    {
        await _service.CreateNotificationAsync(new CreateNotificationRequest
        {
            Title = "Invoice Created: #4711",
            Message = "Your invoice was created in SAP.",
            Category = "Invoice",
            EntityType = "Invoice",
            EntityId = "4711",
            TargetUsername = "tanaka",
            WebhookEvent = WebhookEventTypes.InvoiceCreated,
            Data = new Dictionary<string, string> { ["docEntry"] = "9002" }
        });

        var published = Assert.Single(_webhooks.Published);
        Assert.Equal(WebhookEventTypes.InvoiceCreated, published.EventType);

        var data = Assert.IsType<Dictionary<string, object?>>(published.Payload);
        Assert.Equal("Invoice", data["entityType"]);
        Assert.Equal("4711", data["entityId"]);
        Assert.Equal("9002", data["docEntry"]);
    }

    [Fact]
    public async Task A_notification_with_no_event_publishes_nothing()
    {
        // The overwhelming majority of the 50-odd notification sites stay like this.
        await _service.CreateNotificationAsync(new CreateNotificationRequest
        {
            Title = "Batch status changed",
            Message = "Batch 12 is now Released.",
            Category = "Stock",
            TargetRole = "Admin"
        });

        Assert.Empty(_webhooks.Published);
    }

    [Fact]
    public async Task An_unknown_event_name_publishes_nothing_and_does_not_fail_the_notification()
    {
        var dto = await _service.CreateNotificationAsync(new CreateNotificationRequest
        {
            Title = "Something happened",
            Message = "...",
            Category = "System",
            TargetRole = "Admin",
            WebhookEvent = "invoice.creted"
        });

        Assert.Empty(_webhooks.Published);

        // The notification is the caller's actual job and must survive a bad event name.
        Assert.NotEqual(0, dto.Id);
        Assert.Single(await _context.Notifications.ToListAsync());
    }

    [Fact]
    public void The_invoice_created_factory_carries_the_event_for_every_path_that_uses_it()
    {
        // Set on the factory rather than at its three call sites — the API's own handler, desktop
        // sales consolidation and the reservation service — so none of them can forget it.
        var request = WorkflowNotificationFactory.CreateInvoiceCreatedNotification(
            targetUserId: Guid.NewGuid(),
            targetUsername: "tanaka",
            invoice: new InvoiceDto { DocEntry = 9002, DocNum = 4711, CardCode = "C001", CardName = "Acme" },
            reservationId: null,
            actionUrl: "/invoices",
            fiscalization: null);

        Assert.Equal(WebhookEventTypes.InvoiceCreated, request.WebhookEvent);
    }

    [Fact]
    public async Task Clearing_the_event_on_a_repeat_notification_suppresses_a_duplicate()
    {
        // What the consolidation loop does: one consolidated invoice notifies each van-sales seller,
        // but only the first carries the event. Three sellers must not mean three invoice.created.
        var first = InvoiceNotificationFor("tanaka");
        var second = InvoiceNotificationFor("rudo");
        second.WebhookEvent = null;

        await _service.CreateNotificationAsync(first);
        await _service.CreateNotificationAsync(second);

        Assert.Equal(2, await _context.Notifications.CountAsync());
        var published = Assert.Single(_webhooks.Published);
        Assert.Equal(WebhookEventTypes.InvoiceCreated, published.EventType);
    }

    private static CreateNotificationRequest InvoiceNotificationFor(string username)
        => WorkflowNotificationFactory.CreateInvoiceCreatedNotification(
            targetUserId: null,
            targetUsername: username,
            invoice: new InvoiceDto { DocEntry = 9002, DocNum = 4711, CardCode = "C001", CardName = "Acme" },
            reservationId: null,
            actionUrl: "/mobile-drafts",
            fiscalization: null);

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private sealed class SilentPushService : IPushNotificationService
    {
        public Task<DeviceRegistrationDto> RegisterDeviceAsync(Guid userId, RegisterDeviceRequest request, CancellationToken ct = default) => Task.FromResult(new DeviceRegistrationDto());
        public Task UnregisterDeviceAsync(Guid userId, string deviceToken, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<DeviceRegistrationDto>> GetUserDevicesAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(new List<DeviceRegistrationDto>());
        public Task<int> SendToUserAsync(Guid userId, string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> SendToUsernameAsync(string username, string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> SendToRoleAsync(string role, string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> SendSilentDataToRoleAsync(string role, Dictionary<string, string> data, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> SendToAllAsync(string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> SendToDeviceTokensAsync(IReadOnlyCollection<string> deviceTokens, string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default) => Task.FromResult(0);
        public Task CleanupStaleTokensAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class SilentHubContext : IHubContext<NotificationHub>
    {
        public IHubClients Clients { get; } = new NoClients();
        public IGroupManager Groups { get; } = new NoGroups();

        private sealed class NoClients : IHubClients
        {
            public IClientProxy All { get; } = new NoProxy();
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new NoProxy();
            public IClientProxy Client(string connectionId) => new NoProxy();
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new NoProxy();
            public IClientProxy Group(string groupName) => new NoProxy();
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new NoProxy();
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => new NoProxy();
            public IClientProxy User(string userId) => new NoProxy();
            public IClientProxy Users(IReadOnlyList<string> userIds) => new NoProxy();
        }

        private sealed class NoProxy : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
                => Task.CompletedTask;
        }

        private sealed class NoGroups : IGroupManager
        {
            public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }
}
