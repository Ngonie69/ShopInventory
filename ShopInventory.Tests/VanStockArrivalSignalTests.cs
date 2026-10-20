using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Events.StockTransferReceived;
using ShopInventory.Features.VanSalesCompatibility.Events.StockTransferReceived;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A van's handset only learns that a load landed after its morning position if something tells it.
/// The transfer webhook raises the event; this is the half that decides whose handset, and how many
/// times. Both ways it can be wrong are silent on a handset: the wrong audience is a rep who never
/// hears about their own load, and one signal per line is an app whose silent pushes Android starts
/// to drop.
/// </summary>
public sealed class VanStockArrivalSignalTests : IDisposable
{
    private const string Van = "VAN001";
    private const string OtherVan = "VAN004";
    private const string Depot = "KEFBYC";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly RecordingPushService _push = new();
    private readonly NoOpNotificationService _notices = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public VanStockArrivalSignalTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _cache.Dispose();
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_load_landing_on_a_van_wakes_the_reps_who_drive_it_and_nobody_else()
    {
        var driver = await AddUserAsync(ApplicationRoles.Adr, Van);
        var relief = await AddUserAsync(ApplicationRoles.Sales, Van);
        await AddUserAsync(ApplicationRoles.Adr, OtherVan);
        await AddUserAsync(ApplicationRoles.Adr, Van, isActive: false);
        await AddUserAsync(ApplicationRoles.DepotController, Van);
        await AddUserAsync(ApplicationRoles.CartVendor, Van);

        await Handler().Handle(Arrival(Van, docEntry: 7001, docNum: 9001), default);

        var signal = Assert.Single(_push.SilentPushes);
        Assert.Equal(new[] { driver.Id, relief.Id }.OrderBy(id => id), signal.Users.OrderBy(id => id));
    }

    [Fact]
    public async Task The_signal_names_the_load_and_nothing_a_rep_would_read()
    {
        await AddUserAsync(ApplicationRoles.Adr, Van);

        await Handler().Handle(Arrival(Van, docEntry: 7001, docNum: 9001), default);

        var signal = Assert.Single(_push.SilentPushes);
        Assert.Equal(StockTransferReceivedHandler.ChangeType, signal.Data["changeType"]);
        Assert.Equal(Van, signal.Data["warehouseCode"]);
        Assert.Equal(Depot, signal.Data["fromWarehouse"]);
        Assert.Equal("7001", signal.Data["transferDocEntry"]);
        Assert.Equal("9001", signal.Data["transferDocNum"]);
        Assert.True(signal.Data.ContainsKey("changedAtUtc"));
        Assert.DoesNotContain("title", signal.Data.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("body", signal.Data.Keys, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The listener sends a transfer one line at a time. Forty lines is one load, and one wake.
    /// </summary>
    [Fact]
    public async Task Every_line_of_one_transfer_raises_one_signal()
    {
        await AddUserAsync(ApplicationRoles.Adr, Van);
        var handler = Handler();

        await handler.Handle(Arrival(Van, docEntry: 7001, docNum: 9001, itemCode: "GOU015"), default);
        await handler.Handle(Arrival(Van, docEntry: 7001, docNum: 9001, itemCode: "FET020"), default);
        await handler.Handle(Arrival(Van, docEntry: 7001, docNum: 9001, itemCode: "CHE011"), default);

        Assert.Single(_push.SilentPushes);
    }

    [Fact]
    public async Task A_second_transfer_is_its_own_signal()
    {
        await AddUserAsync(ApplicationRoles.Adr, Van);
        var handler = Handler();

        await handler.Handle(Arrival(Van, docEntry: 7001, docNum: 9001), default);
        await handler.Handle(Arrival(Van, docEntry: 7002, docNum: 9002), default);

        Assert.Equal(2, _push.SilentPushes.Count);
        Assert.Equal("7001", _push.SilentPushes[0].Data["transferDocEntry"]);
        Assert.Equal("7002", _push.SilentPushes[1].Data["transferDocEntry"]);
    }

    /// <summary>
    /// The same document landing on two vans — split loads are booked as one transfer per van, but
    /// the window is keyed by warehouse so that a shared document entry could never silence the
    /// second van.
    /// </summary>
    [Fact]
    public async Task The_window_is_per_warehouse()
    {
        await AddUserAsync(ApplicationRoles.Adr, Van);
        await AddUserAsync(ApplicationRoles.Adr, OtherVan);
        var handler = Handler();

        await handler.Handle(Arrival(Van, docEntry: 7001, docNum: 9001), default);
        await handler.Handle(Arrival(OtherVan, docEntry: 7001, docNum: 9001), default);

        Assert.Equal(2, _push.SilentPushes.Count);
    }

    [Fact]
    public async Task A_load_landing_where_no_van_is_driven_signals_nobody()
    {
        await AddUserAsync(ApplicationRoles.Adr, Van);
        await AddUserAsync(ApplicationRoles.Cashier, "KEFSHOP");

        await Handler().Handle(Arrival("KEFSHOP", docEntry: 7001, docNum: 9001), default);

        Assert.Empty(_push.SilentPushes);
    }

    [Fact]
    public async Task A_warehouse_assignment_matches_whatever_its_case()
    {
        var driver = await AddUserAsync(ApplicationRoles.Adr, "van001");

        await Handler().Handle(Arrival(Van, docEntry: 7001, docNum: 9001), default);

        var signal = Assert.Single(_push.SilentPushes);
        Assert.Equal(driver.Id, Assert.Single(signal.Users));
    }

    /// <summary>
    /// The ledger row is committed before the event is raised, and the listener would re-send a line
    /// whose webhook it saw fail. A push that cannot go out is logged and dropped, never thrown.
    /// </summary>
    [Fact]
    public async Task A_push_that_fails_does_not_fail_the_event()
    {
        await AddUserAsync(ApplicationRoles.Adr, Van);
        _push.FailNext = true;

        var exception = await Record.ExceptionAsync(() => Handler().Handle(Arrival(Van, docEntry: 7001, docNum: 9001), default));

        Assert.Null(exception);
    }

    // --- The visible notice, beside the signal ---

    /// <summary>
    /// A Sales rep is the one who can open the transfer, so a Sales rep is the one told. The ADR on
    /// the same van is woken for the ledger and nothing more: they have no transfers screen.
    /// </summary>
    [Fact]
    public async Task A_load_landing_on_a_van_tells_its_sales_reps_and_not_its_adr()
    {
        await AddUserAsync(ApplicationRoles.Adr, Van);
        var rep = await AddUserAsync(ApplicationRoles.Sales, Van);
        await AddUserAsync(ApplicationRoles.Sales, OtherVan);

        await Handler().Handle(Arrival(Van, docEntry: 7001, docNum: 9001), default);

        var notice = Assert.Single(_notices.Sent);
        Assert.Equal(rep.Id, notice.TargetUserId);
        Assert.Equal(rep.Username, notice.TargetUsername);
        Assert.Null(notice.TargetRole);
        Assert.Single(_push.SilentPushes);
    }

    /// <summary>
    /// The handset opens a tap on <c>InventoryTransfer</c> plus an id on the transfer whose DocEntry
    /// it is, so those two are the contract. And it must not carry the signal's <c>changeType</c>:
    /// the handset would take it for the ledger signal and show the rep nothing.
    /// </summary>
    [Fact]
    public async Task The_notice_names_the_transfer_the_way_the_handset_opens_it()
    {
        await AddUserAsync(ApplicationRoles.Sales, Van);

        await Handler().Handle(Arrival(Van, docEntry: 7001, docNum: 9001), default);

        var notice = Assert.Single(_notices.Sent);
        Assert.Equal("InventoryTransfer", notice.EntityType);
        Assert.Equal("7001", notice.EntityId);
        Assert.Contains("9001", notice.Message);
        Assert.Contains(Depot, notice.Message);
        Assert.Contains(Van, notice.Title);
        Assert.False(string.IsNullOrWhiteSpace(notice.Title));
        Assert.DoesNotContain("changeType", notice.Data!.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Every_line_of_one_transfer_raises_one_notice()
    {
        await AddUserAsync(ApplicationRoles.Sales, Van);
        var handler = Handler();

        await handler.Handle(Arrival(Van, docEntry: 7001, docNum: 9001, itemCode: "GOU015"), default);
        await handler.Handle(Arrival(Van, docEntry: 7001, docNum: 9001, itemCode: "FET020"), default);
        await handler.Handle(Arrival(Van, docEntry: 7002, docNum: 9002), default);

        Assert.Equal(new[] { "7001", "7002" }, _notices.Sent.Select(notice => notice.EntityId));
    }

    /// <summary>
    /// The rep who raised the request was told when the depot converted it, under this same
    /// transfer. A second notice for one load is noise; the rep on the van who did not ask still
    /// hears. The same stored row is what stops a restart, which forgets the in-memory window, from
    /// telling a rep twice.
    /// </summary>
    [Fact]
    public async Task A_rep_already_told_about_the_transfer_is_not_told_again()
    {
        var requester = await AddUserAsync(ApplicationRoles.Sales, Van);
        var colleague = await AddUserAsync(ApplicationRoles.Sales, Van);
        _context.Notifications.Add(new Notification
        {
            UserId = requester.Id,
            TargetUsername = requester.Username,
            Title = "Stock request #512 is on its way",
            Message = "Transfer #9001 was raised against it.",
            Type = "Success",
            Category = "InventoryTransfer",
            EntityType = "InventoryTransfer",
            EntityId = "7001",
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        await Handler().Handle(Arrival(Van, docEntry: 7001, docNum: 9001), default);

        var notice = Assert.Single(_notices.Sent);
        Assert.Equal(colleague.Id, notice.TargetUserId);
    }

    /// <summary>
    /// Without a document entry there is no transfer to open and no way to hold a load to one
    /// notice. The ledger still needs to hear.
    /// </summary>
    [Fact]
    public async Task A_line_with_no_document_entry_signals_but_tells_nobody()
    {
        await AddUserAsync(ApplicationRoles.Sales, Van);

        await Handler().Handle(Arrival(Van, docEntry: null, docNum: null), default);

        Assert.Single(_push.SilentPushes);
        Assert.Empty(_notices.Sent);
    }

    private StockTransferReceivedHandler Handler() => new(
        _context,
        _push,
        _notices,
        _cache,
        NullLogger<StockTransferReceivedHandler>.Instance);

    private static StockTransferReceivedEvent Arrival(string warehouse, int? docEntry, int? docNum, string itemCode = "GOU015") =>
        new(warehouse, Depot, itemCode, "Gouda 1kg", 30m, docEntry, docNum, DateTime.UtcNow);

    private async Task<User> AddUserAsync(string role, string warehouse, bool isActive = true)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.test",
            PasswordHash = "x",
            Role = role,
            IsActive = isActive
        };
        user.SetWarehouseCodes([warehouse]);

        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }

    private sealed class RecordingPushService : IPushNotificationService
    {
        private readonly List<(IReadOnlyCollection<Guid> Users, Dictionary<string, string> Data)> _silentPushes = [];

        public IReadOnlyList<(IReadOnlyCollection<Guid> Users, Dictionary<string, string> Data)> SilentPushes => _silentPushes;

        public bool FailNext { get; set; }

        public Task<int> SendSilentDataToUsersAsync(IReadOnlyCollection<Guid> userIds, Dictionary<string, string> data, CancellationToken ct = default)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("Firebase is unreachable.");
            }

            _silentPushes.Add((userIds, data));
            return Task.FromResult(userIds.Count);
        }

        // The signal is never news for the rep, and the notice that is goes through
        // INotificationService, so it is stored and can be checked for — never pushed straight.
        public Task<int> SendSilentDataToRoleAsync(string role, Dictionary<string, string> data, CancellationToken ct = default) =>
            throw new InvalidOperationException($"A van stock arrival was pushed to the whole {role} role.");

        public Task<int> SendToUserAsync(Guid userId, string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default) =>
            throw new InvalidOperationException($"A van stock arrival was pushed as a visible notification: \"{title}\".");

        public Task<int> SendToUsernameAsync(string username, string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default) =>
            throw new InvalidOperationException($"A van stock arrival was pushed as a visible notification: \"{title}\".");

        public Task<int> SendToRoleAsync(string role, string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default) =>
            throw new InvalidOperationException($"A van stock arrival was pushed as a visible notification: \"{title}\".");

        public Task<int> SendToAllAsync(string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default) =>
            throw new InvalidOperationException($"A van stock arrival was pushed as a visible notification: \"{title}\".");

        public Task<int> SendToDeviceTokensAsync(IReadOnlyCollection<string> deviceTokens, string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default) =>
            throw new InvalidOperationException($"A van stock arrival was pushed as a visible notification: \"{title}\".");

        public Task<DeviceRegistrationDto> RegisterDeviceAsync(Guid userId, RegisterDeviceRequest request, CancellationToken ct = default) =>
            Task.FromResult(new DeviceRegistrationDto());

        public Task UnregisterDeviceAsync(Guid userId, string deviceToken, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<List<DeviceRegistrationDto>> GetUserDevicesAsync(Guid userId, CancellationToken ct = default) =>
            Task.FromResult(new List<DeviceRegistrationDto>());

        public Task CleanupStaleTokensAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
