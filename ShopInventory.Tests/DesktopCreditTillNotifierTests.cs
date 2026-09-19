using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.DesktopCreditNotes;
using ShopInventory.Hubs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// The push that tells a till a credit was raised against one of its sales.
/// </summary>
public sealed class DesktopCreditTillNotifierTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext _db;
    private readonly List<(string Group, string Method, object?[] Args)> _sent = [];
    private bool _hubThrows;

    public DesktopCreditTillNotifierTests()
    {
        _connection.Open();
        _db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_fiscalised_credit_is_sent_to_the_sale_warehouse_with_what_the_till_matches_on()
    {
        var sale = await GivenSaleAsync(" kefgrs ");
        var credit = await DesktopCreditPosters.GivenCreditAsync(_db, sale);
        await _db.DesktopCreditNotes.Where(n => n.Id == credit.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Currency, "USD"));

        await Notifier().NotifyIssuedAsync(credit.Id, default);

        var (group, method, args) = Assert.Single(_sent);
        // Trimmed and upper-cased, as NotificationHub names the group the till joined.
        Assert.Equal(NotificationHub.WarehouseGroup("KEFGRS"), group);
        Assert.Equal(HubDesktopCreditTillNotifier.MethodName, method);

        var notice = Assert.IsType<DesktopCreditIssuedNotice>(Assert.Single(args));
        Assert.Equal(credit.Number, notice.CreditNoteNumber);
        Assert.Equal("KEF-GRS-20260919-A1", notice.SaleReference);
        Assert.Equal(DesktopSaleNumber.Format(sale.Id), notice.SaleNumber);
        Assert.Equal("KEFGRS", notice.WarehouseCode);
        Assert.Equal(credit.Amount, notice.Amount);
        Assert.Equal("USD", notice.Currency);
        Assert.Equal("Customer return", notice.Reason);
        Assert.Equal("Groombridge Shop", notice.CustomerName);
    }

    [Theory]
    [InlineData(DesktopCreditStatuses.Prepared)]
    [InlineData(DesktopCreditStatuses.Rejected)]
    [InlineData(DesktopCreditStatuses.ReconciliationRequired)]
    public async Task A_credit_zimra_has_not_accepted_is_never_sent(string status)
    {
        var credit = await DesktopCreditPosters.GivenCreditAsync(_db, await GivenSaleAsync("KEFGRS"), status);

        await Notifier().NotifyIssuedAsync(credit.Id, default);

        Assert.Empty(_sent);
    }

    [Fact]
    public async Task A_hub_that_fails_does_not_fail_the_credit()
    {
        var credit = await DesktopCreditPosters.GivenCreditAsync(_db, await GivenSaleAsync("KEFGRS"));
        _hubThrows = true;

        await Notifier().NotifyIssuedAsync(credit.Id, default);
    }

    /// <summary>
    /// The till (KefShop, <c>Records/DesktopCreditIssuedNotice.cs</c>) reads these names, and its
    /// <c>DesktopCreditAlertTests</c> pins the same JSON from its side. A rename here that is not made
    /// there leaves the counter an alert full of blanks and no error anywhere.
    /// </summary>
    [Fact]
    public void The_payload_carries_exactly_the_names_the_till_reads()
    {
        var json = System.Text.Json.JsonSerializer.SerializeToElement(
            new DesktopCreditIssuedNotice(Guid.NewGuid(), "DCN-1", "REF", "INV1", "KEFGRS", 1m, "USD", "r", "c",
                DateTime.UtcNow),
            new JsonHubProtocolOptions().PayloadSerializerOptions);

        Assert.Equal(
            ["amount", "creditNoteId", "creditNoteNumber", "currency", "customerName", "fiscalisedAtUtc",
             "reason", "saleNumber", "saleReference", "warehouseCode"],
            json.EnumerateObject().Select(p => p.Name).Order().ToArray());
    }

    private HubDesktopCreditTillNotifier Notifier() =>
        new(_db, Hub(), NullLogger<HubDesktopCreditTillNotifier>.Instance);

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

    private async Task<DesktopSaleEntity> GivenSaleAsync(string warehouse)
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = "KEF-GRS-20260919-A1",
            SourceSystem = SaleSourceSystems.ShopTill,
            CardCode = "CIS004",
            CardName = "Groombridge Shop",
            DocDate = DateTime.UtcNow.Date,
            TotalAmount = 100m,
            Currency = "USD",
            WarehouseCode = warehouse,
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            FiscalDayNo = "537",
            FiscalReceiptNumber = "221019",
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 0, ItemCode = "ICS025", ItemDescription = "Blueberry",
                    Quantity = 6, UnitPrice = 10m, LineTotal = 60m, WarehouseCode = warehouse.Trim()
                }
            ]
        };

        _db.DesktopSales.Add(sale);
        await _db.SaveChangesAsync();
        return sale;
    }
}
