using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Hubs;
using ShopInventory.Models;
using ShopInventory.Services;
using ShopInventory.Web.Models;

namespace ShopInventory.Tests;

/// <summary>
/// A sales order notification carries structured fields — the order number, the
/// customer, the total, where it came from — alongside its prose message, and the
/// staff toast lays those out instead of parsing the sentence back apart.
///
/// They used to exist only on the SignalR payload. Anything that re-read the
/// notification (the polling fallback when the hub is down, a page reload, the
/// notification panel) got the message and nothing else, so the toast silently
/// dropped to its plain shape. These pin both halves: the fields reach the push,
/// and they survive the round-trip through the database.
///
/// The key names are the other thing held down here. They are written in this
/// project and read in ShopInventory.Web, which does not reference it, so the
/// compiler checks nothing across that seam. ShopInventory.Tests references both
/// and is the only place the two halves can be compared at all.
/// </summary>
public sealed class NotificationDataPersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly CapturingHubContext _hub = new();
    private readonly NotificationService _service;

    public NotificationDataPersistenceTests()
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
            _hub,
            new NoOpPushNotificationService());
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// One notification goes to Admin and one to Cashier, and both carry the
    /// fields — the toast is the same either way.
    /// </summary>
    [Theory]
    [InlineData("role:Admin")]
    [InlineData("role:Cashier")]
    public async Task SalesOrderNotificationPushesTheFieldsTheToastReads(string group)
    {
        await CreateSalesOrderAsync();

        var pushed = Assert.Single(_hub.Sent, sent => sent.Group == group).Payload;
        var data = Assert.IsType<Dictionary<string, string>>(pushed.Data, exactMatch: false);

        Assert.Equal(ExpectedFields, data);
    }

    /// <summary>
    /// The toast reads Data by string key and ShopInventory.Web does not reference
    /// this project, so nothing but agreement holds the two halves together. Rename
    /// a key here and the lookup simply misses: the toast falls back to its plain
    /// title/message shape with no exception, no failing build and no log line.
    ///
    /// Driven off NotificationDataKeys itself rather than a list copied out of it,
    /// so a key added there tomorrow is covered the moment it is added.
    /// </summary>
    [Fact]
    public async Task EveryFieldTheToastReadsIsOnThePush()
    {
        await CreateSalesOrderAsync();

        var data = Assert.Single(_hub.Sent, sent => sent.Group == "role:Admin").Payload.Data!;
        var keys = typeof(NotificationDataKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(member => member.IsLiteral && member.FieldType == typeof(string))
            .Select(member => (string)member.GetRawConstantValue()!)
            .ToArray();

        Assert.NotEmpty(keys);

        foreach (var key in keys)
        {
            Assert.True(data.ContainsKey(key),
                $"NotificationDataKeys.{key} is read by NotificationToast but " +
                $"CreateSalesOrderNotificationAsync no longer produces it. Present keys: " +
                $"{string.Join(", ", data.Keys.OrderBy(k => k, StringComparer.Ordinal))}");

            Assert.False(string.IsNullOrWhiteSpace(data[key]),
                $"'{key}' is present but blank, which NotificationModel.DataValue treats as absent.");
        }
    }

    /// <summary>
    /// Not every order has a username behind it. The key is left off entirely rather
    /// than written blank, which is what lets the toast's provenance line read
    /// "Submitted from Mobile App" instead of trailing an empty "by".
    /// </summary>
    [Fact]
    public async Task AnUnattributedOrderOmitsCreatedByRatherThanSendingItBlank()
    {
        await CreateSalesOrderAsync(createdByUsername: null);

        var data = Assert.Single(_hub.Sent, sent => sent.Group == "role:Admin").Payload.Data!;

        Assert.False(data.ContainsKey("createdBy"));
        Assert.Equal("Mobile App", data["sourceLabel"]);
    }

    [Fact]
    public async Task SalesOrderFieldsSurviveBeingReadBackFromTheDatabase()
    {
        await CreateSalesOrderAsync();
        _context.ChangeTracker.Clear();

        var listed = await _service.GetNotificationsAsync("someone", new[] { "Admin" });

        var notification = Assert.Single(listed.Notifications);
        var data = Assert.IsType<Dictionary<string, string>>(notification.Data, exactMatch: false);

        Assert.Equal("SO-20260802-0001", data["orderNumber"]);
        Assert.Equal("Pick n Pay Aspindale", data["customerName"]);
        Assert.Equal("Mobile App", data["sourceLabel"]);
        Assert.Equal("tgahadza", data["createdBy"]);
    }

    /// <summary>
    /// The Web reads this back with CultureInfo.InvariantCulture, so the stored form
    /// has to be invariant whatever the server's own locale is. Asserted as a round
    /// trip rather than against an exact string — ExpectedFields pins the literal —
    /// because what matters here is that the figure survives: "4502,19" written by a
    /// comma-decimal server parses as 450219 and puts a hundredfold error on the toast.
    /// </summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public async Task TotalRoundTripsForAServerInAnyLocale(string serverCulture)
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(serverCulture);

        try
        {
            // Guards the guard: if the ambient culture were not actually reaching the
            // service, every case here would pass on en-US formatting and prove nothing.
            // Written against the same call the producer makes, so it fails if dropping
            // InvariantCulture there would no longer be detectable here.
            if (serverCulture != "en-US")
            {
                Assert.NotEqual("4502.19", 4502.19m.ToString());
            }

            await CreateSalesOrderAsync();
            _context.ChangeTracker.Clear();

            var listed = await _service.GetNotificationsAsync("someone", new[] { "Admin" });
            var stored = Assert.Single(listed.Notifications).Data!["docTotal"];

            Assert.True(
                decimal.TryParse(stored, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed),
                $"Stored as '{stored}' under {serverCulture}, which the Web cannot parse invariantly.");
            Assert.Equal(4502.19m, parsed);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// Rows written before the Data column existed carry null, and the read path
    /// has to hand that back rather than throwing a whole page of notifications.
    /// </summary>
    [Fact]
    public async Task ANotificationWithoutDataStillReadsBack()
    {
        _context.Notifications.Add(new Notification
        {
            Title = "Raised before the column existed",
            Message = "No structured fields on this one",
            Type = "Info",
            Category = "System",
            TargetRole = "Admin",
            Data = null
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var listed = await _service.GetNotificationsAsync("someone", new[] { "Admin" });

        Assert.Null(Assert.Single(listed.Notifications).Data);
    }

    private Task CreateSalesOrderAsync(string? createdByUsername = "tgahadza") => _service.CreateSalesOrderNotificationAsync(
        orderId: 41,
        orderNumber: "SO-20260802-0001",
        customerCode: "C0042",
        customerName: "Pick n Pay Aspindale",
        docTotal: 4502.19m,
        status: "Open",
        source: "Mobile",
        createdByUsername: createdByUsername);

    /// <summary>
    /// Every field the order above should put on the wire. Asserted whole rather than
    /// key by key, so a key silently dropped fails as loudly as one renamed, and the
    /// orderId/cardCode/customerCode/status the rest of the app relies on are pinned
    /// alongside the six the toast reads.
    /// </summary>
    private static Dictionary<string, string> ExpectedFields => new(StringComparer.OrdinalIgnoreCase)
    {
        ["orderId"] = "41",
        ["orderNumber"] = "SO-20260802-0001",
        ["cardCode"] = "C0042",
        ["customerCode"] = "C0042",
        ["customerName"] = "Pick n Pay Aspindale",
        ["status"] = "Open",
        ["source"] = "Mobile",
        ["sourceLabel"] = "Mobile App",
        // Ungrouped as well as invariant: this is a value to be parsed, and where the
        // separators go is a rendering decision the toast makes. Invariance regardless of
        // the server's locale is held down by TotalRoundTripsForAServerInAnyLocale; this
        // pins the exact form the toast is handed.
        ["docTotal"] = "4502.19",
        ["createdBy"] = "tgahadza"
    };

    /// <summary>
    /// Records what the service broadcasts. Data never reaches SignalR as anything
    /// but part of the DTO, so capturing the argument is the only way to see it.
    /// Split in two because IHubContext and IHubClients both spell Clients and
    /// Groups, as a property on one and a method on the other.
    /// </summary>
    private sealed class CapturingHubContext : IHubContext<NotificationHub>
    {
        private readonly CapturingClients _clients = new();

        public List<(string Group, NotificationDto Payload)> Sent => _clients.Sent;

        public IHubClients Clients => _clients;

        public IGroupManager Groups { get; } = new NoOpGroupManager();
    }

    private sealed class CapturingClients : IHubClients, IClientProxy
    {
        private string _currentGroup = string.Empty;

        public List<(string Group, NotificationDto Payload)> Sent { get; } = new();

        public IClientProxy Group(string groupName)
        {
            _currentGroup = groupName;
            return this;
        }

        public IClientProxy Groups(IReadOnlyList<string> groupNames)
        {
            _currentGroup = string.Join(",", groupNames);
            return this;
        }

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            if (method == "ReceiveNotification" && args.FirstOrDefault() is NotificationDto dto)
            {
                Sent.Add((_currentGroup, dto));
            }

            return Task.CompletedTask;
        }

        public IClientProxy All => this;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => this;
        public IClientProxy Client(string connectionId) => this;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => this;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => this;
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

    private sealed class NoOpPushNotificationService : IPushNotificationService
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

        public Task<int> SendToDeviceTokensAsync(
            IReadOnlyCollection<string> deviceTokens,
            string title,
            string body,
            Dictionary<string, string>? data = null,
            CancellationToken ct = default)
            => Task.FromResult(0);

        public Task CleanupStaleTokensAsync(CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
