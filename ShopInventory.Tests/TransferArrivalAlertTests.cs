using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.InventoryTransfers.Commands.AnnounceTransferArrivals;
using ShopInventory.Hubs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A till is told when SAP books a stock transfer into its warehouse — once, and only for stock
/// arriving.
/// </summary>
public sealed class TransferArrivalAlertTests : IDisposable
{
    private const string Shop = "KEFSHOP";
    private const string OtherShop = "KEFGRS";
    private const string Vending = "CORMACH";
    private const string Depot = "KEFGRC";

    // 20 Oct 2026 09:00 CAT.
    private static readonly DateTime Now = new(2026, 10, 20, 7, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext _db;
    private readonly List<SapStockMovementDocument> _sap = [];
    private readonly List<(IReadOnlyCollection<string> Warehouses, DateTime From, DateTime To)> _sapReads = [];
    private readonly List<(string Group, string Method, object?[] Args)> _sent = [];
    private bool _sapThrows;
    private bool _hubThrows;

    public TransferArrivalAlertTests()
    {
        _connection.Open();
        _db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _db.Shops.Add(new ShopEntity { Code = "SHP", Name = "Factory shop", BusinessPartnerCode = "CIS006", WarehouseCode = Shop });
        _db.Shops.Add(new ShopEntity { Code = "GRS", Name = "Groombridge", BusinessPartnerCode = "CIS004", WarehouseCode = OtherShop });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── The first pass ───────────────────────────────────

    [Fact]
    public async Task The_first_pass_records_what_SAP_already_holds_without_announcing_it()
    {
        GivenTransfer(129001, 50001, Depot, Shop, ("VHU002", 320));

        var result = await PassAsync(Now);

        Assert.Equal(0, result.Announced);
        Assert.Equal(1, result.RecordedSilently);
        Assert.Empty(_sent);
        var notice = await _db.TransferArrivalNotices.SingleAsync(n => n.TransferDocEntry == 129001);
        Assert.Null(notice.AnnouncedAt);
    }

    [Fact]
    public async Task A_transfer_booked_after_the_first_pass_is_announced_to_its_warehouse()
    {
        GivenTransfer(129001, 50001, Depot, Shop, ("VHU002", 320));
        await PassAsync(Now);

        GivenTransfer(129199, 50077, Depot, Shop, ("VHU002", 320), ("MUE001", 48), ("YOG020", 10));
        var result = await PassAsync(Now.AddMinutes(2));

        Assert.Equal(1, result.Announced);
        var (group, method, args) = Assert.Single(_sent);
        Assert.Equal(NotificationHub.WarehouseGroup(Shop), group);
        Assert.Equal("ReceiveNotification", method);

        var notice = Assert.IsType<NotificationDto>(Assert.Single(args));
        Assert.Equal("Stock transfer #50077 from KEFGRC", notice.Title);
        Assert.Equal("3 items transferred into KEFSHOP. Open Inventory → Transfers to check and receive it.", notice.Message);
        Assert.Equal("InventoryTransfer", notice.Category);
        Assert.Equal("129199", notice.EntityId);
        Assert.Equal("50077", notice.Data!["transferDocNum"]);

        var row = await _db.TransferArrivalNotices.SingleAsync(n => n.TransferDocEntry == 129199);
        Assert.Equal(Now.AddMinutes(2), row.AnnouncedAt);
    }

    /// <summary>
    /// Without the marker, a first pass that found nothing would leave the table empty, and the next
    /// pass would take the first real arrival for a backlog and swallow it.
    /// </summary>
    [Fact]
    public async Task A_first_pass_that_finds_nothing_still_starts_watching()
    {
        await PassAsync(Now);

        GivenTransfer(129199, 50077, Depot, Shop, ("VHU002", 320));
        var result = await PassAsync(Now.AddMinutes(2));

        Assert.Equal(1, result.Announced);
        Assert.Single(_sent);
    }

    // ── Once ─────────────────────────────────────────────

    [Fact]
    public async Task A_transfer_is_announced_once_however_many_passes_see_it()
    {
        await PassAsync(Now);
        GivenTransfer(129199, 50077, Depot, Shop, ("VHU002", 320));

        await PassAsync(Now.AddMinutes(2));
        var second = await PassAsync(Now.AddMinutes(4));

        Assert.Equal(0, second.Announced);
        Assert.Single(_sent);
    }

    [Fact]
    public async Task A_hub_that_fails_does_not_fail_the_pass_or_send_twice()
    {
        await PassAsync(Now);
        GivenTransfer(129199, 50077, Depot, Shop, ("VHU002", 320));
        _hubThrows = true;

        var first = await PassAsync(Now.AddMinutes(2));
        _hubThrows = false;
        var second = await PassAsync(Now.AddMinutes(4));

        Assert.Equal(1, first.Announced);
        Assert.Equal(0, second.Announced);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task SAP_that_cannot_be_read_records_nothing_so_the_next_pass_announces_it()
    {
        await PassAsync(Now);
        GivenTransfer(129199, 50077, Depot, Shop, ("VHU002", 320));
        _sapThrows = true;

        var failed = await Handler().Handle(new AnnounceTransferArrivalsCommand(Now.AddMinutes(2)), default);
        _sapThrows = false;
        var next = await PassAsync(Now.AddMinutes(4));

        Assert.True(failed.IsError);
        Assert.Equal(1, next.Announced);
        Assert.Single(_sent);
    }

    // ── What counts as arriving ──────────────────────────

    [Fact]
    public async Task Stock_leaving_a_shop_is_not_announced_there()
    {
        await PassAsync(Now);
        GivenTransfer(129200, 50078, Shop, Depot, ("VHU002", 12));

        var result = await PassAsync(Now.AddMinutes(2));

        Assert.Equal(0, result.Announced);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task A_transfer_between_two_shops_is_announced_to_the_receiving_one_only()
    {
        await PassAsync(Now);
        GivenTransfer(129201, 50079, OtherShop, Shop, ("YOG020", 10));

        await PassAsync(Now.AddMinutes(2));

        var (group, _, args) = Assert.Single(_sent);
        Assert.Equal(NotificationHub.WarehouseGroup(Shop), group);
        Assert.Equal("Stock transfer #50079 from KEFGRS", Assert.IsType<NotificationDto>(Assert.Single(args)).Title);
    }

    /// <summary>Cancelling a transfer into the shop writes negative lines, which take the stock back out.</summary>
    [Fact]
    public async Task Cancelling_a_transfer_into_a_shop_is_not_announced_as_an_arrival()
    {
        await PassAsync(Now);
        GivenTransfer(129202, 50080, Depot, Shop, ("VHU002", -320));

        var result = await PassAsync(Now.AddMinutes(2));

        Assert.Equal(0, result.Announced);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task A_line_naming_the_shop_counts_even_when_the_header_names_another_warehouse()
    {
        await PassAsync(Now);
        _sap.Add(new SapStockMovementDocument
        {
            DocEntry = 129203,
            DocNum = 50081,
            FromWarehouse = Depot,
            ToWarehouse = OtherShop,
            StockTransferLines =
            [
                new SapStockMovementLine { ItemCode = "VHU002", Quantity = 5, FromWarehouseCode = Depot, WarehouseCode = Shop }
            ]
        });

        await PassAsync(Now.AddMinutes(2));

        Assert.Equal(NotificationHub.WarehouseGroup(Shop), Assert.Single(_sent).Group);
    }

    [Fact]
    public async Task Each_item_is_counted_once_however_many_lines_carry_it()
    {
        await PassAsync(Now);
        GivenTransfer(129204, 50082, Depot, Shop, ("VHU002", 100), ("vhu002", 20));

        await PassAsync(Now.AddMinutes(2));

        var notice = Assert.IsType<NotificationDto>(Assert.Single(Assert.Single(_sent).Args));
        Assert.StartsWith("1 item transferred into KEFSHOP.", notice.Message);
    }

    // ── Which warehouses are watched ─────────────────────

    [Fact]
    public async Task Active_shops_and_vending_accounts_are_watched_and_nothing_else()
    {
        _db.Shops.Add(new ShopEntity { Code = "OLD", Name = "Closed", BusinessPartnerCode = "CIS009", WarehouseCode = "KEFOLD", IsActive = false });
        _db.Users.AddRange(
            User("blessing", ApplicationRoles.CartVendor, active: true, $"[\"{Vending.ToLowerInvariant()} \"]"),
            User("gone", ApplicationRoles.CartVendor, active: false, "[\"KEFGONE\"]"),
            User("ruvimbo", ApplicationRoles.StockController, active: true, "[\"KEFSTOCK\"]"));
        await _db.SaveChangesAsync();

        await PassAsync(Now);

        Assert.Equal(new[] { Vending, OtherShop, Shop }, Assert.Single(_sapReads).Warehouses.Order());
    }

    [Fact]
    public async Task A_vending_warehouse_is_announced_by_its_upper_cased_group()
    {
        _db.Users.Add(User("blessing", ApplicationRoles.CartVendor, active: true, "[\"cormach\"]"));
        await _db.SaveChangesAsync();
        await PassAsync(Now);
        GivenTransfer(129205, 50083, Depot, Vending, ("ICS025", 24));

        await PassAsync(Now.AddMinutes(2));

        Assert.Equal("warehouse:CORMACH", Assert.Single(_sent).Group);
    }

    [Fact]
    public async Task SAP_is_asked_for_yesterday_and_today_in_CAT()
    {
        // 00:30 CAT on 20 Oct is still the 19th in UTC.
        await PassAsync(new DateTime(2026, 10, 19, 22, 30, 0, DateTimeKind.Utc));

        var read = Assert.Single(_sapReads);
        Assert.Equal(new DateTime(2026, 10, 19), read.From);
        Assert.Equal(new DateTime(2026, 10, 20), read.To);
    }

    // ── The till's side ──────────────────────────────────

    /// <summary>
    /// The till (KefShop, <c>Records/PlatformNotice.cs</c>) binds <c>ReceiveNotification</c> by these
    /// names and shows the title and message as they are.
    /// </summary>
    [Fact]
    public void The_payload_carries_the_names_the_till_reads()
    {
        var json = System.Text.Json.JsonSerializer.SerializeToElement(
            AnnounceTransferArrivalsHandler.Notice(new TransferArrival(1, 2, Shop, Depot, 3), Now),
            new JsonHubProtocolOptions().PayloadSerializerOptions);

        foreach (var name in new[] { "id", "title", "message", "type", "category", "entityType", "entityId", "createdAt", "data" })
        {
            Assert.True(json.TryGetProperty(name, out _), $"missing '{name}'");
        }
    }

    // ── Helpers ──────────────────────────────────────────

    private async Task<AnnounceTransferArrivalsResult> PassAsync(DateTime utcNow)
    {
        var result = await Handler().Handle(new AnnounceTransferArrivalsCommand(utcNow), default);
        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private void GivenTransfer(int docEntry, int docNum, string from, string to, params (string Item, double Quantity)[] lines) =>
        _sap.Add(new SapStockMovementDocument
        {
            DocEntry = docEntry,
            DocNum = docNum,
            FromWarehouse = from,
            ToWarehouse = to,
            StockTransferLines = lines
                .Select(line => new SapStockMovementLine
                {
                    ItemCode = line.Item,
                    Quantity = line.Quantity,
                    FromWarehouseCode = from,
                    WarehouseCode = to
                })
                .ToList()
        });

    private static User User(string username, string role, bool active, string warehouses) => new()
    {
        Username = username,
        PasswordHash = "x",
        Role = role,
        IsActive = active,
        AssignedWarehouseCodes = warehouses
    };

    private AnnounceTransferArrivalsHandler Handler() =>
        new(_db, Sap(), Hub(), NullLogger<AnnounceTransferArrivalsHandler>.Instance);

    private ISAPServiceLayerClient Sap() => StubProxy.For<ISAPServiceLayerClient>((method, args) =>
    {
        if (method.Name != nameof(ISAPServiceLayerClient.GetStockTransfersCreatedAsync))
            throw new InvalidOperationException($"ISAPServiceLayerClient.{method.Name} was not expected.");
        if (_sapThrows)
            return Task.FromException<List<SapStockMovementDocument>>(new HttpRequestException("SAP down"));

        _sapReads.Add(((IReadOnlyCollection<string>)args![0]!, (DateTime)args[1]!, (DateTime)args[2]!));
        return Task.FromResult(_sap.ToList());
    });

    /// <summary>
    /// <c>Clients.Group(g).SendAsync(m, arg)</c> lands on <c>IClientProxy.SendCoreAsync(m, [arg])</c>,
    /// so that is the one call recorded.
    /// </summary>
    private IHubContext<NotificationHub> Hub()
    {
        IClientProxy GroupProxy(string group) => StubProxy.For<IClientProxy>((method, args) =>
        {
            if (method.Name != nameof(IClientProxy.SendCoreAsync))
                throw new InvalidOperationException($"IClientProxy.{method.Name} was not expected.");
            if (_hubThrows) throw new InvalidOperationException("hub down");
            _sent.Add((group, (string)args![0]!, (object?[])args[1]!));
            return Task.CompletedTask;
        });

        var clients = StubProxy.For<IHubClients>((method, args) => method.Name == nameof(IHubClients.Group)
            ? GroupProxy((string)args![0]!)
            : throw new InvalidOperationException($"IHubClients.{method.Name} was not expected."));

        return StubProxy.For<IHubContext<NotificationHub>>((method, _) => method.Name == "get_Clients"
            ? clients
            : throw new InvalidOperationException($"IHubContext.{method.Name} was not expected."));
    }
}
