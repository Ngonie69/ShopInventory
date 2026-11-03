using System.Net;
using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DailySalesWhatsApp.Commands.SendDailySalesWhatsApp;
using ShopInventory.Features.DailySalesWhatsApp.Commands.UpdateDailySalesWhatsAppSettings;
using ShopInventory.Features.DailySalesWhatsApp.Queries.GetDailySalesWhatsAppReport;
using ShopInventory.Features.DailySalesWhatsApp.Queries.GetDailySalesWhatsAppSettings;
using ShopInventory.Features.VanSalesReports.Queries.GetVanDailyTruckReport;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using Xunit.Abstractions;

namespace ShopInventory.Tests;

/// <summary>
/// The daily sales report on WhatsApp: van takings per route as the daily truck report states them, one
/// shop's quantities for a fixed list of products, and a morning send that reaches each number once.
/// </summary>
public sealed class DailySalesWhatsAppTests : IDisposable
{
    // Thursday 29 Oct 2026, 07:15 CAT: the first pass for Wednesday the 28th.
    private static readonly DateTimeOffset ThursdayMorning = new(2026, 10, 29, 5, 15, 0, TimeSpan.Zero);
    private static readonly DateTime Tuesday = new(2026, 10, 27);
    private static readonly DateTime Wednesday = new(2026, 10, 28);

    private static readonly Guid CbdRep = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid East1Rep = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid East2Rep = Guid.Parse("10000000-0000-0000-0000-000000000003");
    private static readonly Guid UpcRep = Guid.Parse("10000000-0000-0000-0000-000000000004");
    private static readonly Guid West1Rep = Guid.Parse("10000000-0000-0000-0000-000000000005");
    private static readonly Guid LooseRep = Guid.Parse("10000000-0000-0000-0000-000000000006");

    private const string Manager = "+263771000001";
    private const string Director = "+263772000002";

    private readonly ITestOutputHelper _output;
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly FakeOpenWAClient _gateway = new();
    private readonly ManualClock _clock = new();
    private OpenWASettings _openWa = CustomerDocumentTestKit.Gateway();
    private int _reference;

    public DailySalesWhatsAppTests(ITestOutputHelper output)
    {
        _output = output;
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        var cbd = new RouteEntity { Code = "CBDCZA", Name = "CBD/CZA Truck", Territory = "Harare" };
        var east1 = new RouteEntity { Code = "EAST1", Name = "East 1", Territory = "Harare" };
        var east2 = new RouteEntity { Code = "EAST2", Name = "East 2", Territory = "Harare" };
        var upc1 = new RouteEntity { Code = "UPC1", Name = "Upc 1", Territory = "UPC" };
        var west1 = new RouteEntity { Code = "WEST1", Name = "West 1 Truck", Territory = "Harare" };
        _context.Routes.AddRange(cbd, east1, east2, upc1, west1);
        _context.Shops.Add(new ShopEntity { Code = "KEF", Name = "Kefalos Factory Shop", BusinessPartnerCode = "CIS006", WarehouseCode = "KEFSHOP", IsActive = true });
        _context.Shops.Add(new ShopEntity { Code = "OTH", Name = "Other Shop", BusinessPartnerCode = "CIS007", WarehouseCode = "OTHSHOP", IsActive = true });
        _context.SaveChanges();

        AddRep(CbdRep, "VAN001", cbd);
        AddRep(East1Rep, "VAN002", east1);
        AddRep(East2Rep, "VAN003", east2);
        AddRep(UpcRep, "VAN004", upc1);
        AddRep(West1Rep, "VAN005", west1);
        AddRep(LooseRep, "VAN099", null);
        _context.SaveChanges();

        AddRouteDay(CbdRep, cbd, null);
        AddRouteDay(East1Rep, east1, null);
        AddRouteDay(East2Rep, east2, null);
        AddRouteDay(UpcRep, upc1, "Zaka");
        AddRouteDay(West1Rep, west1, null);

        AddVanSale(CbdRep, "VAN001", 600m);
        AddVanSale(CbdRep, "VAN001", 400m);
        AddVanSale(CbdRep, "VAN001", 300m, currency: "ZiG");
        AddVanSale(East1Rep, "VAN002", 800m);
        AddVanSale(East2Rep, "VAN003", 671m);
        AddVanSale(UpcRep, "VAN004", 2629m);
        AddVanSale(West1Rep, "VAN005", 1556m);
        AddVanSale(LooseRep, "VAN099", 50m);

        // The shop: 157 tubs of Cortina over two sales and 463 yoghurts, $5,446.16 in all with the other item.
        AddShopSale(Wednesday, "KEFSHOP", 2000m, ("CORT5L", 100m), ("YOG200", 463m));
        AddShopSale(Wednesday, "KEFSHOP", 1446.16m, ("cort5l ", 57m));
        AddShopSale(Wednesday, "KEFSHOP", 2000m, ("SCOOP6", 110m));
        // None of these is the shop's day: another day, another shop, the vending depot at the same warehouse, ZiG.
        AddShopSale(Tuesday, "KEFSHOP", 999m, ("CORT5L", 999m));
        AddShopSale(Wednesday, "OTHSHOP", 999m, ("CORT5L", 999m));
        AddShopSale(Wednesday, "KEFSHOP", 999m, source: SaleSourceSystems.Vending, lines: [("CORT5L", 999m)]);
        AddShopSale(Wednesday, "KEFSHOP", 3000m, currency: "ZiG", lines: [("YOG200", 10m)]);
        _context.SaveChanges();

        _clock.Advance(ThursdayMorning - _clock.GetUtcNow());
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ---- The van table ----------------------------------------------------------------------------

    [Fact]
    public async Task With_nothing_set_up_every_route_is_listed_and_upcountry_carries_its_area()
    {
        var report = await ReportAsync();

        Assert.Equal(
            [
                ("CBD/CZA Truck", 1000m), ("East 1", 800m), ("East 2", 671m), ("West 1 Truck", 1556m),
                ("Upc 1 - Zaka", 2629m), ("No route", 50m)
            ],
            report.VanLines.Select(line => (line.Label, line.Sales)));
        Assert.Equal(6706m, report.VanTotal);
        Assert.Equal([new DailySalesWhatsAppCurrencyAmount("ZIG", 300m)], report.VanOtherCurrencies);
        Assert.Null(report.Shop);
    }

    [Fact]
    public async Task Van_rows_rename_merge_and_order_routes_and_an_unlisted_route_is_still_counted()
    {
        await SaveAsync(vanRows:
        [
            Row("CBD / Chitungwiza", "CBDCZA"),
            Row("East 1 & 2", "east1", "EAST2"),
            Row("UPC 1", showArea: true, "UPC1"),
            Row("Bulawayo Local", "BYOADR")
        ]);

        var report = await ReportAsync();

        Assert.Equal(
            [
                ("CBD / Chitungwiza", 1000m), ("East 1 & 2", 1471m), ("UPC 1 - Zaka", 2629m), ("Bulawayo Local", 0m),
                ("Other routes", 1556m), ("No route", 50m)
            ],
            report.VanLines.Select(line => (line.Label, line.Sales)));

        // The total is every truck the truck report saw, whichever rows were set up.
        var trucks = await TruckReportAsync();
        Assert.Equal(trucks.Days.Sum(day => day.Sales) + trucks.SalesOutsideRoutes, report.VanTotal);
    }

    // ---- The shop table ---------------------------------------------------------------------------

    [Fact]
    public async Task The_shop_lists_its_products_in_order_with_zeros_and_totals_every_dollar_sale()
    {
        await SaveAsync(
            shop: "KEFSHOP",
            productRows:
            [
                Row("5Ltr Cortina Ice-cream", "CORT5L"),
                Row("Shamiso Vanilla", "SHAV5L"),
                Row("200ml drinking yogurt", "YOG200", "YOG200B"),
            ]);

        var shop = (await ReportAsync()).Shop!;

        Assert.Equal("Kefalos Factory Shop Sales", shop.Heading);
        // Units are units whatever they were paid in: the 10 yoghurts sold for ZiG count, the ZiG takings do not.
        Assert.Equal(
            [("5Ltr Cortina Ice-cream", 157m), ("Shamiso Vanilla", 0m), ("200ml drinking yogurt", 473m)],
            shop.Lines.Select(line => (line.Label, line.Quantity)));
        Assert.Equal(5446.16m, shop.Total);
        Assert.Equal([new DailySalesWhatsAppCurrencyAmount("ZIG", 3000m)], shop.OtherCurrencies);
    }

    [Fact]
    public async Task The_message_reads_like_the_office_sheet()
    {
        await SaveAsync(
            shop: "KEFSHOP",
            heading: "Kefalos Shop Sales",
            vanRows: [Row("CBD / Chitungwiza", "CBDCZA"), Row("East 1 & 2", "EAST1", "EAST2"), Row("West 1", "WEST1"), Row("UPC 1", showArea: true, "UPC1")],
            productRows: [Row("5Ltr Cortina Ice-cream", "CORT5L"), Row("6 pack scoop", "SCOOP6"), Row("150ml scoop", "SCOOP150")]);

        var message = (await ReportAsync()).Message;
        _output.WriteLine(message);

        Assert.Equal(
            """
            *Van Daily Sales (Wednesday 28.10.2026)*
            CBD / Chitungwiza: $1,000.00
            East 1 & 2: $1,471.00
            West 1: $1,556.00
            UPC 1 - Zaka: $2,629.00
            No route: $50.00
            *TOTAL: $6,706.00*
            _Plus ZIG 300.00, not in the total_

            *Kefalos Shop Sales (Wednesday 28.10.2026)*
            5Ltr Cortina Ice-cream: 157
            6 pack scoop: 110
            150ml scoop: 0
            *Total: $5,446.16*
            _Plus ZIG 3,000.00, not in the total_
            """.ReplaceLineEndings("\n"),
            message);
    }

    // ---- Settings ---------------------------------------------------------------------------------

    [Fact]
    public async Task Saving_normalises_numbers_and_codes_and_reads_back_what_was_saved()
    {
        var saved = await SaveAsync(
            enabled: true,
            recipients: ["0771 000 001", "+263 77 100 0001", "263772000002"],
            shop: "KEFSHOP",
            vanRows: [Row(" East 1 & 2 ", showArea: true, " east1", "EAST2", "east2")],
            productRows: [Row("Yoghurt", "yog200")]);

        Assert.True(saved.Enabled);
        Assert.Equal([Manager, Director], saved.Recipients);
        Assert.Equal("Kefalos Factory Shop", saved.ShopName);
        Assert.Equal(("East 1 & 2", "EAST1,EAST2", true), Flat(saved.VanRows.Single()));
        Assert.Equal(("Yoghurt", "YOG200", false), Flat(saved.ProductRows.Single()));
        Assert.Equal("admin", saved.UpdatedBy);
    }

    [Fact]
    public async Task A_number_that_is_not_a_phone_or_a_shop_that_does_not_exist_is_refused()
    {
        var number = await UpdateHandler().Handle(Command(recipients: ["call me"]), CancellationToken.None);
        Assert.Equal("DailySalesWhatsApp.InvalidNumber", number.FirstError.Code);

        var shop = await UpdateHandler().Handle(Command(shop: "NOWHERE"), CancellationToken.None);
        Assert.Equal("DailySalesWhatsApp.UnknownShop", shop.FirstError.Code);

        Assert.False(await _context.SystemConfigs.AnyAsync(config => config.Category == "DailySalesWhatsApp"));
    }

    [Fact]
    public void Switching_it_on_needs_a_number_and_every_row_needs_a_code()
    {
        var validator = new UpdateDailySalesWhatsAppSettingsValidator();

        Assert.Contains(validator.Validate(Command(enabled: true, recipients: [])).Errors,
            error => error.ErrorMessage.Contains("at least one WhatsApp number"));
        Assert.Contains(validator.Validate(Command(productRows: [new DailySalesWhatsAppRow("Yoghurt", [])])).Errors,
            error => error.ErrorMessage.Contains("'Yoghurt' needs at least one item code"));
        Assert.True(validator.Validate(Command(enabled: true, recipients: [Manager], productRows: [Row("Yoghurt", "YOG200")])).IsValid);
    }

    // ---- Sending ----------------------------------------------------------------------------------

    [Fact]
    public async Task The_morning_pass_sends_yesterday_to_each_number_once()
    {
        await SaveAsync(enabled: true, recipients: [Manager, Director], shop: "KEFSHOP", productRows: [Row("Cortina", "CORT5L")]);

        var first = await SendAsync(scheduled: true);

        Assert.Equal(Wednesday, first.TradingDate);
        Assert.Equal([Manager, Director], first.SentTo);
        Assert.Equal(["263771000001@c.us", "263772000002@c.us"], _gateway.Texts.Select(text => text.ChatId));
        Assert.All(_gateway.Texts, text => Assert.StartsWith("*Van Daily Sales (Wednesday 28.10.2026)*", text.Text));

        var settings = await SettingsAsync();
        Assert.Equal(Wednesday, settings.LastSentFor);
        Assert.Equal([Manager, Director], settings.LastSentTo);

        _clock.Advance(TimeSpan.FromMinutes(15));
        var second = await SendAsync(scheduled: true);

        Assert.True(second.Skipped);
        Assert.Equal(2, _gateway.Texts.Count);
    }

    [Fact]
    public async Task A_refused_number_is_tried_again_on_the_next_pass_and_nobody_else_is_sent_twice()
    {
        await SaveAsync(enabled: true, recipients: [Manager, Director]);
        _gateway.OnSendText = text => text.ChatId.StartsWith("263772")
            ? Task.FromException<WhatsAppMessageDispatchDto>(FakeOpenWAClient.Refusal(HttpStatusCode.ServiceUnavailable, "session not ready"))
            : Task.FromResult(new WhatsAppMessageDispatchDto { MessageId = "ok", Timestamp = 1 });

        var first = await SendAsync(scheduled: true);

        Assert.Equal([Manager], first.SentTo);
        Assert.Equal(Director, Assert.Single(first.Failed).Number);

        _gateway.OnSendText = null;
        _clock.Advance(TimeSpan.FromMinutes(15));
        var second = await SendAsync(scheduled: true);

        Assert.Equal([Director], second.SentTo);
        Assert.Equal(["263771000001@c.us", "263772000002@c.us", "263772000002@c.us"], _gateway.Texts.Select(text => text.ChatId));
        Assert.Equal([Manager, Director], (await SettingsAsync()).LastSentTo);
    }

    [Fact]
    public async Task A_send_whatsapp_may_have_delivered_is_never_sent_again()
    {
        await SaveAsync(enabled: true, recipients: [Manager]);
        _gateway.OnSendText = _ => Task.FromException<WhatsAppMessageDispatchDto>(new TaskCanceledException("timed out"));

        var first = await SendAsync(scheduled: true);
        Assert.Equal([Manager], first.Unconfirmed);

        _gateway.OnSendText = null;
        var second = await SendAsync(scheduled: true);

        Assert.True(second.Skipped);
        Assert.Single(_gateway.Texts);
    }

    [Fact]
    public async Task Switched_off_or_without_a_gateway_the_pass_sends_nothing_and_a_later_one_can()
    {
        await SaveAsync(enabled: false, recipients: [Manager]);
        Assert.Equal("the report is switched off", (await SendAsync(scheduled: true)).Reason);

        await SaveAsync(enabled: true, recipients: [Manager]);
        _openWa = CustomerDocumentTestKit.Gateway(configured: false);
        Assert.Equal("WhatsApp is not set up on this server", (await SendAsync(scheduled: true)).Reason);
        Assert.Empty(_gateway.Texts);

        _openWa = CustomerDocumentTestKit.Gateway();
        Assert.Equal([Manager], (await SendAsync(scheduled: true)).SentTo);
    }

    [Fact]
    public async Task A_person_can_send_any_day_to_any_number_and_it_counts_for_the_morning_pass()
    {
        await SaveAsync(enabled: true, recipients: [Manager, Director]);

        var manual = await SendAsync(scheduled: false, recipients: ["0771000001"]);
        Assert.Equal([Manager], manual.SentTo);

        var scheduled = await SendAsync(scheduled: true);
        Assert.Equal([Director], scheduled.SentTo);

        var older = await SendAsync(scheduled: false, day: Tuesday);
        Assert.Equal(Tuesday, older.TradingDate);
        Assert.Contains("(Tuesday 27.10.2026)", _gateway.Texts.Last().Text);

        // Resending an older day leaves the record of who has yesterday's alone.
        var settings = await SettingsAsync();
        Assert.Equal(Wednesday, settings.LastSentFor);
        Assert.Equal([Manager, Director], settings.LastSentTo);
        Assert.True((await SendAsync(scheduled: true)).Skipped);
    }

    [Fact]
    public async Task The_number_that_sends_never_receives()
    {
        _gateway.SessionPhone = "263771000001";
        await SaveAsync(enabled: true, recipients: [Manager, Director]);

        var result = await SendAsync(scheduled: true);

        Assert.Equal([Director], result.SentTo);
        Assert.Contains("is the number WhatsApp messages are sent from", Assert.Single(result.Failed).Reason);
        Assert.Single(_gateway.Texts);
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    private static DailySalesWhatsAppRow Row(string label, params string[] codes) => new(label, codes.ToList());

    private static DailySalesWhatsAppRow Row(string label, bool showArea, params string[] codes) => new(label, codes.ToList(), showArea);

    /// <summary>A row as plain values: records compare their code lists by reference.</summary>
    private static (string, string, bool) Flat(DailySalesWhatsAppRow row) => (row.Label, string.Join(",", row.Codes), row.ShowArea);

    private static UpdateDailySalesWhatsAppSettingsCommand Command(
        bool enabled = false,
        List<string>? recipients = null,
        string? shop = null,
        string? heading = null,
        List<DailySalesWhatsAppRow>? vanRows = null,
        List<DailySalesWhatsAppRow>? productRows = null) =>
        new("admin", enabled, recipients ?? [], shop, heading, vanRows ?? [], productRows ?? []);

    private async Task<DailySalesWhatsAppSettings> SaveAsync(
        bool enabled = false,
        List<string>? recipients = null,
        string? shop = null,
        string? heading = null,
        List<DailySalesWhatsAppRow>? vanRows = null,
        List<DailySalesWhatsAppRow>? productRows = null)
    {
        var result = await UpdateHandler().Handle(Command(enabled, recipients, shop, heading, vanRows, productRows), CancellationToken.None);
        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        _context.ChangeTracker.Clear();
        return result.Value;
    }

    private async Task<DailySalesWhatsAppSettings> SettingsAsync() =>
        (await new GetDailySalesWhatsAppSettingsHandler(_context).Handle(new GetDailySalesWhatsAppSettingsQuery(), CancellationToken.None)).Value;

    private async Task<DailySalesWhatsAppReport> ReportAsync()
    {
        var result = await new GetDailySalesWhatsAppReportHandler(_context, Mediator(), _clock)
            .Handle(new GetDailySalesWhatsAppReportQuery(Wednesday), CancellationToken.None);
        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private async Task<VanDailyTruckReportResult> TruckReportAsync() =>
        (await TruckHandler().Handle(new GetVanDailyTruckReportQuery(Wednesday, Wednesday), CancellationToken.None)).Value;

    private async Task<DailySalesWhatsAppSendResult> SendAsync(bool scheduled, List<string>? recipients = null, DateTime? day = null)
    {
        var result = await new SendDailySalesWhatsAppHandler(
                _context, Mediator(), _gateway, Options.Create(_openWa), Options.Create(new CustomerDocumentDeliverySettings()),
                new RecordingAuditService(), NullLogger<SendDailySalesWhatsAppHandler>.Instance, _clock)
            .Handle(new SendDailySalesWhatsAppCommand(scheduled, day, recipients, "admin"), CancellationToken.None);
        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        _context.ChangeTracker.Clear();
        return result.Value;
    }

    private UpdateDailySalesWhatsAppSettingsHandler UpdateHandler() =>
        new(_context, Mediator(), new RecordingAuditService(), Options.Create(new CustomerDocumentDeliverySettings()),
            NullLogger<UpdateDailySalesWhatsAppSettingsHandler>.Instance);

    private GetVanDailyTruckReportHandler TruckHandler() =>
        new(_context, Options.Create(new TaxSettings()), new StoredSaleInvoiceCostReader(_context));

    /// <summary>Every query the report reads, run for real against the test database.</summary>
    private IMediator Mediator()
    {
        IMediator mediator = null!;
        mediator = StubProxy.For<IMediator>((method, args) => method.Name != nameof(IMediator.Send) ? null : args?[0] switch
        {
            GetDailySalesWhatsAppSettingsQuery settings =>
                new GetDailySalesWhatsAppSettingsHandler(_context).Handle(settings, CancellationToken.None),
            GetDailySalesWhatsAppReportQuery report =>
                new GetDailySalesWhatsAppReportHandler(_context, mediator, _clock).Handle(report, CancellationToken.None),
            GetVanDailyTruckReportQuery trucks =>
                TruckHandler().Handle(trucks, CancellationToken.None),
            _ => null
        });
        return mediator;
    }

    private void AddRep(Guid id, string account, RouteEntity? route) =>
        _context.Users.Add(new User
        {
            Id = id,
            Username = account.ToLowerInvariant(),
            Email = $"{account}@example.com",
            PasswordHash = "x",
            Role = "Sales",
            IsActive = true,
            AssignedWarehouseCode = account,
            AssignedBusinessPartnerCode = account,
            RouteId = route?.Id
        });

    private void AddRouteDay(Guid rep, RouteEntity route, string? area) =>
        _context.VanRouteDays.Add(new VanRouteDayEntity
        {
            UserId = rep,
            Username = rep.ToString(),
            TradingDate = Wednesday,
            RouteId = route.Id,
            RouteCode = route.Code,
            RouteName = route.Name,
            Territory = route.Territory,
            DepartedAt = DateTime.SpecifyKind(Wednesday.AddHours(5), DateTimeKind.Utc),
            AreaName = area
        });

    private void AddVanSale(Guid rep, string account, decimal total, string currency = "USD") =>
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = $"VAN-{++_reference}",
            SourceSystem = SaleSourceSystems.VanSales,
            CardCode = account,
            CardName = account,
            RouteCustomerCode = "SHOP1",
            RouteCustomerName = "Shop 1",
            DocDate = Wednesday,
            TotalAmount = total,
            VatAmount = 0m,
            Currency = currency,
            WarehouseCode = account,
            PaymentMethod = "Cash",
            AmountPaid = total,
            CreatedBy = rep.ToString(),
            Lines = [new DesktopSaleLineEntity { LineNum = 0, ItemCode = "CHE011", Quantity = 1, UnitPrice = total, LineTotal = total, WarehouseCode = account }]
        });

    private void AddShopSale(DateTime day, string warehouse, decimal total, params (string ItemCode, decimal Quantity)[] lines) =>
        AddShopSale(day, warehouse, total, SaleSourceSystems.ShopTill, "USD", lines);

    private void AddShopSale(
        DateTime day,
        string warehouse,
        decimal total,
        string source = SaleSourceSystems.ShopTill,
        string currency = "USD",
        (string ItemCode, decimal Quantity)[]? lines = null) =>
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = $"TILL-{++_reference}",
            SourceSystem = source,
            CardCode = "CIS006",
            CardName = "Factory Kefalos shop POS USD",
            WarehouseCode = warehouse,
            DocDate = day,
            TotalAmount = total,
            VatAmount = 0m,
            AmountPaid = total,
            Currency = currency,
            PaymentMethod = TenderTypes.Cash,
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            CreatedBy = Guid.NewGuid().ToString(),
            CreatedAt = DateTime.SpecifyKind(day.AddHours(10), DateTimeKind.Utc),
            Lines = (lines ?? [])
                .Select((line, index) => new DesktopSaleLineEntity
                {
                    LineNum = index + 1,
                    ItemCode = line.ItemCode,
                    Quantity = line.Quantity,
                    UnitPrice = 1m,
                    LineTotal = line.Quantity,
                    WarehouseCode = warehouse
                })
                .ToList()
        });
}
