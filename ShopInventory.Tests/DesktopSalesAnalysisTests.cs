using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the till takings analysis: that money is split by the tender it was actually paid with, that
/// nothing is counted twice or added across currencies, and that it reads exactly the shops the sales list
/// lets the caller read.
/// </summary>
/// <remarks>
/// Run against SQLite rather than an in-memory provider, so a grouping that cannot be translated fails
/// here instead of quietly being evaluated row by row in production.
/// </remarks>
public sealed class DesktopSalesAnalysisTests : IDisposable
{
    private static readonly DateTime Day1 = new(2026, 9, 1);
    private static readonly DateTime Day2 = new(2026, 9, 2);

    private const string FarmPartner = "FARM-BP";
    private const string FarmPartnerName = "Farm Counter Sales";
    private const string MachipisaPartner = "MACH-BP";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    private readonly Guid _adminId = Guid.NewGuid();
    private readonly Guid _cashierId = Guid.NewGuid();
    private readonly Guid _otherOperatorId = Guid.NewGuid();
    private readonly int _farmId;

    public DesktopSalesAnalysisTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();

        var farm = new ShopEntity
        {
            Code = "FARM",
            Name = "Farm",
            BusinessPartnerCode = "FARM-BP",
            WarehouseCode = "KEFSHOP",
            IsActive = true,
        };
        var machipisa = new ShopEntity
        {
            Code = "MACHIPISA",
            Name = "Machipisa",
            BusinessPartnerCode = "MACH-BP",
            WarehouseCode = "CORMACH2",
            IsActive = true,
        };
        _context.Shops.AddRange(farm, machipisa);
        _context.SaveChanges();
        _farmId = farm.Id;

        _context.Users.AddRange(
            new User
            {
                Id = _adminId,
                Username = "admin",
                PasswordHash = "x",
                Role = ApplicationRoles.Admin,
                IsActive = true,
            },
            new User
            {
                Id = _cashierId,
                Username = "rudo",
                FirstName = "Rudo",
                LastName = "Moyo",
                PasswordHash = "x",
                Role = ApplicationRoles.Cashier,
                IsActive = true,
            },
            new User
            {
                Id = _otherOperatorId,
                Username = "tendai.m",
                PasswordHash = "x",
                Role = ApplicationRoles.Cashier,
                IsActive = true,
            });

        _context.DesktopSales.AddRange(
            // Farm, in US dollars: every tender, in the spellings tills have actually stored.
            Sale("F-CASH-1", TenderTypes.Cash, 10m, Day1, utcHour: 8, paid: 20m,
                lines: [("MILK", 2, 8.70m)]),
            Sale("F-CASH-2", "cash", 15m, Day2, utcHour: 9,
                lines: [("MILK", 1, 4.35m), ("BREAD", 3, 8.70m)]),
            Sale("F-SWIPE-1", TenderTypes.Swipe, 30m, Day1, utcHour: 10, paymentReference: "SLIP-9",
                lines: [("BREAD", 10, 26.10m)]),
            // The same partner under no name at all, and under an older spelling of it: neither is a
            // second partner.
            Sale("F-ECO-1", TenderTypes.Ecocash, 20m, Day2, utcHour: 8, paymentReference: "MP240902.0850.A1",
                cardName: null, lines: [("CHEESE", 1, 17.40m)]),
            Sale("F-ECO-2", "ecocash", 5m, Day2, utcHour: 12,
                cardName: "Farm Counter (old card name)", lines: [("MILK", 1, 4.35m)]),
            Sale("F-NONE", null, 7m, Day1, utcHour: 12, paid: 0m),
            Sale("F-LEGACY", "transfer", 3m, Day1, utcHour: 12, source: SaleSourceSystems.LegacyDesktop),

            // The same shop in ZWG, which must never be added to the dollars.
            Sale("F-ZWG", TenderTypes.Cash, 100m, Day1, utcHour: 12, currency: "ZWG"),

            // Another shop, rung up by another operator, as a partner whose sales carry no name.
            Sale("M-CASH", TenderTypes.Cash, 50m, Day1, utcHour: 12, warehouse: "CORMACH2", createdBy: _otherOperatorId,
                cardCode: MachipisaPartner, cardName: null),

            // Outside the period.
            Sale("F-OUT", TenderTypes.Cash, 77m, new DateTime(2026, 8, 31), utcHour: 12));

        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ---- The payment method breakdown -------------------------------------------------------------

    [Fact]
    public async Task Takings_are_split_by_the_tender_they_were_paid_with()
    {
        var usd = Dollars(await AnalyseAsAdmin());

        Assert.Equal(140m, usd.TotalAmount);
        Assert.Equal(8, usd.SalesCount);

        AssertMethod(usd, TenderTypes.Cash, count: 3, total: 75m);
        AssertMethod(usd, TenderTypes.Swipe, count: 1, total: 30m);
        AssertMethod(usd, TenderTypes.Ecocash, count: 2, total: 25m);
        AssertMethod(usd, "Transfer", count: 1, total: 3m);
        AssertMethod(usd, TenderTypes.NotRecorded, count: 1, total: 7m);

        // The rows account for every dollar, so nothing fell between them.
        Assert.Equal(usd.TotalAmount, usd.ByPaymentMethod.Sum(row => row.TotalAmount));
    }

    [Fact]
    public async Task A_tender_in_the_tills_own_casing_is_not_a_payment_method_of_its_own()
    {
        // "cash" and "ecocash" were stored by tills before the handler normalised on the way in. Grouped
        // on the raw column they would be two more lines, each looking complete.
        var result = (await AnalyseAsAdmin()).Value;

        Assert.Equal(
            new[] { TenderTypes.Cash, TenderTypes.Swipe, TenderTypes.Ecocash, "Transfer", TenderTypes.NotRecorded },
            result.PaymentMethods);
    }

    [Fact]
    public async Task Cash_swipe_and_ecocash_are_stated_even_when_none_was_taken()
    {
        // ZWG was only ever paid in cash. A report that left Swipe and EcoCash out would be silent where
        // it should say "none".
        var zwg = (await AnalyseAsAdmin()).Value.Currencies.Single(c => c.Currency == "ZWG");

        AssertMethod(zwg, TenderTypes.Cash, count: 1, total: 100m);
        AssertMethod(zwg, TenderTypes.Swipe, count: 0, total: 0m);
        AssertMethod(zwg, TenderTypes.Ecocash, count: 0, total: 0m);
    }

    [Fact]
    public async Task Each_method_states_its_share_of_the_takings()
    {
        var cash = Method(Dollars(await AnalyseAsAdmin()), TenderTypes.Cash);

        Assert.Equal(53.6m, cash.ShareOfValuePercent); // 75 of 140
        Assert.Equal(37.5m, cash.ShareOfCountPercent); // 3 of 8
        Assert.Equal(25m, cash.AverageSale);
    }

    [Fact]
    public async Task Neither_the_amount_tendered_nor_the_change_is_reported()
    {
        // F-CASH-1 was paid 20 for a 10 sale. What was handed over, and the change, are not takings: stated
        // beside them they were read as a second, disagreeing total.
        var json = System.Text.Json.JsonSerializer.Serialize(
            (await AnalyseAsAdmin()).Value, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.DoesNotContain("\"amountPaid\"", json);
        Assert.DoesNotContain("\"changeGiven\"", json);
        Assert.Contains("\"totalAmount\"", json);
    }

    [Fact]
    public async Task An_ecocash_sale_with_no_reference_is_counted()
    {
        // The reference is what ties a wallet receipt to money that arrived, so one missing is worth a
        // number on the page.
        Assert.Equal(1, Method(Dollars(await AnalyseAsAdmin()), TenderTypes.Ecocash).WithoutReferenceCount);
    }

    // ---- One payment method -----------------------------------------------------------------------

    [Fact]
    public async Task A_payment_method_confines_every_figure_to_it_whatever_the_till_spelled()
    {
        // F-ECO-1 is stored "Ecocash" and F-ECO-2 "ecocash"; asking in either spelling reads both.
        var usd = Dollars(await Analyse(_adminId, Day1, Day2, paymentMethod: "ecocash"));

        Assert.Equal(25m, usd.TotalAmount);
        Assert.Equal(2, usd.SalesCount);
        AssertMethod(usd, TenderTypes.Ecocash, count: 2, total: 25m);
        AssertMethod(usd, TenderTypes.Cash, count: 0, total: 0m);

        // The hours and items are confined too, which the page cannot work out from a split.
        Assert.Equal(new[] { "CHEESE", "MILK" }, usd.TopItems.Select(i => i.ItemCode));
        Assert.Equal(25m, usd.ByHour.Sum(h => h.TotalAmount));
        Assert.Equal(new[] { Day2 }, usd.ByDay.Select(d => d.Date));
    }

    [Fact]
    public async Task A_payment_method_folds_legacy_spellings_like_the_breakdown_does()
    {
        var result = (await Analyse(_adminId, Day1, Day2, paymentMethod: TenderTypes.Cash)).Value;

        Assert.Equal(TenderTypes.Cash, result.PaymentMethod);
        Assert.Equal(75m, Dollars(result).TotalAmount); // "Cash", "cash" and the other shop's "Cash"
        Assert.Equal(100m, result.Currencies.Single(c => c.Currency == "ZWG").TotalAmount);
    }

    [Fact]
    public async Task Not_recorded_selects_the_sales_no_tender_was_stored_for()
    {
        var usd = Dollars(await Analyse(_adminId, Day1, Day2, paymentMethod: TenderTypes.NotRecorded));

        Assert.Equal(7m, usd.TotalAmount);
        Assert.Equal(1, usd.SalesCount);
    }

    [Fact]
    public async Task A_payment_method_nobody_paid_with_is_an_empty_analysis_not_everything()
    {
        var result = (await Analyse(_adminId, Day1, Day2, paymentMethod: TenderTypes.Innbucks)).Value;

        Assert.Empty(result.Currencies);
    }

    // ---- The period before ------------------------------------------------------------------------

    [Fact]
    public async Task Each_currency_states_the_same_number_of_days_just_before()
    {
        var result = (await AnalyseAsAdmin()).Value;

        Assert.Equal(new DateTime(2026, 8, 30), result.PreviousFromDate);
        Assert.Equal(new DateTime(2026, 8, 31), result.PreviousToDate);

        var usd = Dollars(result);
        Assert.Equal(1, usd.PreviousSalesCount); // F-OUT
        Assert.Equal(77m, usd.PreviousTotalAmount);

        // ZWG sold nothing before, which is a zero rather than a missing section.
        Assert.Equal(0m, result.Currencies.Single(c => c.Currency == "ZWG").PreviousTotalAmount);
    }

    [Fact]
    public async Task The_period_before_is_read_under_the_same_filters()
    {
        var byDay = Dollars(await Analyse(_adminId, Day2, Day2));
        Assert.Equal(100m, byDay.PreviousTotalAmount); // Day1, every tender, both shops

        var cashOnly = Dollars(await Analyse(_adminId, Day2, Day2, paymentMethod: TenderTypes.Cash));
        Assert.Equal(60m, cashOnly.PreviousTotalAmount);

        var otherShop = Dollars(await Analyse(_adminId, Day2, Day2, warehouseCode: "KEFSHOP"));
        Assert.Equal(50m, otherShop.PreviousTotalAmount);
    }

    // ---- Nothing counted twice, nothing added across currencies -----------------------------------

    [Fact]
    public async Task Currencies_are_never_added_together()
    {
        var result = (await AnalyseAsAdmin()).Value;

        Assert.Equal(new[] { "USD", "ZWG" }, result.Currencies.Select(c => c.Currency));
        Assert.Equal(100m, result.Currencies.Single(c => c.Currency == "ZWG").TotalAmount);
    }

    [Fact]
    public async Task Online_van_sales_are_counted_once_from_their_receipt()
    {
        // The receipt row is the only record of an online van sale this report reads, so leaving it out
        // would drop the sale from the analysis rather than save it from being counted twice.
        _context.DesktopSales.Add(
            Sale("VAN-ONLINE", TenderTypes.Cash, 999m, Day1, utcHour: 12, source: SaleSourceSystems.VanSalesOnline));
        await _context.SaveChangesAsync();

        var usd = Dollars((await AnalyseAsAdmin()).Value);

        Assert.Equal(140m + 999m, usd.TotalAmount);
        Assert.Equal(8 + 1, usd.SalesCount);
        var online = Assert.Single(usd.BySource, row => row.Key == SaleSourceSystems.VanSalesOnline);
        Assert.Equal(1, online.SalesCount);
        Assert.Equal(999m, online.TotalAmount);
        Assert.Equal("Van sales (online)", online.Label);
    }

    [Fact]
    public async Task Naming_a_source_reads_that_source_alone()
    {
        var result = (await Analyse(_adminId, Day1, Day2, sourceSystem: SaleSourceSystems.LegacyDesktop)).Value;

        var usd = Dollars(result);
        Assert.Equal(1, usd.SalesCount);
        Assert.Equal(3m, usd.TotalAmount);
    }

    [Fact]
    public async Task A_sale_outside_the_period_is_left_out()
    {
        var usd = Dollars(await Analyse(_adminId, Day2, Day2));

        Assert.Equal(40m, usd.TotalAmount); // F-CASH-2, F-ECO-1, F-ECO-2
        Assert.Equal(1, usd.DaysTraded);
    }

    // ---- Whose takings ----------------------------------------------------------------------------

    [Fact]
    public async Task A_cashier_at_a_shop_is_confined_to_its_own_takings()
    {
        var cashierAtFarm = await AddUser(ApplicationRoles.Cashier, shopId: _farmId);

        var result = await Analyse(cashierAtFarm, Day1, Day2);

        Assert.False(result.IsError);
        Assert.Equal("KEFSHOP", result.Value.WarehouseCode);
        Assert.Equal(90m, Dollars(result).TotalAmount);
    }

    [Fact]
    public async Task A_cashier_at_a_shop_naming_another_shop_is_refused()
    {
        var cashierAtFarm = await AddUser(ApplicationRoles.Cashier, shopId: _farmId);

        var result = await Analyse(cashierAtFarm, Day1, Day2, warehouseCode: "CORMACH2");

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.SalesReadOutsideScope", result.FirstError.Code);
    }

    [Fact]
    public async Task A_warehouse_role_cannot_read_the_analysis()
    {
        var storeman = await AddUser(ApplicationRoles.StockController, shopId: null);

        var result = await Analyse(storeman, Day1, Day2);

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.SalesReadNotPermitted", result.FirstError.Code);
    }

    // ---- The other breakdowns ---------------------------------------------------------------------

    [Fact]
    public async Task Each_day_carries_its_own_split_by_tender()
    {
        var usd = Dollars(await AnalyseAsAdmin());

        var day1 = usd.ByDay.Single(d => d.Date == Day1);
        Assert.Equal(100m, day1.TotalAmount);
        Assert.Equal(60m, day1.ByPaymentMethod.Single(p => p.PaymentMethod == TenderTypes.Cash).TotalAmount);
        Assert.Equal(30m, day1.ByPaymentMethod.Single(p => p.PaymentMethod == TenderTypes.Swipe).TotalAmount);
        Assert.Equal(0m, day1.ByPaymentMethod.Single(p => p.PaymentMethod == TenderTypes.Ecocash).TotalAmount);

        Assert.Equal(40m, usd.ByDay.Single(d => d.Date == Day2).TotalAmount);
    }

    [Fact]
    public async Task The_hours_are_the_counters_own_clock()
    {
        // F-CASH-1 and F-ECO-1 were rung up at 08:15 UTC, which is 10:15 at the counter.
        var usd = Dollars(await AnalyseAsAdmin());

        var ten = usd.ByHour.Single(h => h.Hour == 10);
        Assert.Equal(2, ten.SalesCount);
        Assert.Equal(30m, ten.TotalAmount);
        Assert.DoesNotContain(usd.ByHour, h => h.Hour == 8);
    }

    [Fact]
    public async Task Shops_sources_and_operators_are_each_broken_down()
    {
        var usd = Dollars(await AnalyseAsAdmin());

        Assert.Equal(new[] { "KEFSHOP", "CORMACH2" }, usd.ByWarehouse.Select(w => w.Key));
        Assert.Equal(90m, usd.ByWarehouse[0].TotalAmount);

        Assert.Contains(usd.BySource, s => s.Label == "Shop till");
        Assert.Contains(usd.BySource, s => s.Label == "Desktop (legacy)");

        // Named, rather than left as the account id a sale is stamped with.
        Assert.Equal("Rudo Moyo", usd.ByOperator.Single(o => o.Key == _cashierId.ToString()).Label);
        Assert.Equal("tendai.m", usd.ByOperator.Single(o => o.Key == _otherOperatorId.ToString()).Label);
    }

    [Fact]
    public async Task Business_partners_are_broken_down_by_the_name_their_sales_carried()
    {
        var result = await AnalyseAsAdmin();
        var usd = Dollars(result);

        Assert.Equal(new[] { FarmPartner, MachipisaPartner }, usd.ByBusinessPartner.Select(p => p.Key));

        // Named, and by the name most of its sales carried: one carried no name and one an older
        // spelling, and neither splits the partner into two rows or renames it.
        var farm = usd.ByBusinessPartner[0];
        Assert.Equal(FarmPartnerName, farm.Label);
        Assert.Equal(7, farm.SalesCount);
        Assert.Equal(90m, farm.TotalAmount);
        Assert.Equal(64.3m, farm.ShareOfValuePercent);
        Assert.Equal(25m, farm.ByPaymentMethod.Single(p => p.PaymentMethod == TenderTypes.Cash).TotalAmount);
        Assert.Equal(30m, farm.ByPaymentMethod.Single(p => p.PaymentMethod == TenderTypes.Swipe).TotalAmount);
        Assert.Equal(25m, farm.ByPaymentMethod.Single(p => p.PaymentMethod == TenderTypes.Ecocash).TotalAmount);

        // A partner none of whose sales carried a name is still listed, by its code.
        var machipisa = usd.ByBusinessPartner[1];
        Assert.Equal(MachipisaPartner, machipisa.Label);
        Assert.Equal(1, machipisa.SalesCount);
        Assert.Equal(50m, machipisa.TotalAmount);

        // Its ZWG sales are its own row in the ZWG section, never added to the dollars.
        var zwg = result.Value.Currencies.Single(c => c.Currency == "ZWG").ByBusinessPartner.Single();
        Assert.Equal((FarmPartnerName, 100m), (zwg.Label, zwg.TotalAmount));
    }

    [Fact]
    public async Task Best_sellers_rank_by_net_value_and_count_sales_rather_than_lines()
    {
        var usd = Dollars(await AnalyseAsAdmin());

        // CHEESE and MILK tie on value, and the tie is broken by code so the order is stable.
        Assert.Equal(new[] { "BREAD", "CHEESE", "MILK" }, usd.TopItems.Select(i => i.ItemCode));

        var bread = usd.TopItems[0];
        Assert.Equal(34.80m, bread.NetAmount);
        Assert.Equal(13m, bread.Quantity);
        Assert.Equal(2, bread.SalesCount);

        Assert.Equal(3, usd.TopItems.Single(i => i.ItemCode == "MILK").SalesCount);
        Assert.Equal(3, usd.DistinctItems);
        Assert.Equal(18m, usd.QuantitySold);
    }

    // ---- The period -------------------------------------------------------------------------------

    [Fact]
    public void A_period_that_ends_before_it_starts_is_refused()
    {
        var validation = new GetDesktopSalesAnalysisValidator()
            .Validate(new GetDesktopSalesAnalysisQuery(Guid.NewGuid(), Day2, Day1));

        Assert.False(validation.IsValid);
    }

    [Fact]
    public void More_than_a_year_at_once_is_refused()
    {
        var validation = new GetDesktopSalesAnalysisValidator()
            .Validate(new GetDesktopSalesAnalysisQuery(Guid.NewGuid(), new DateTime(2025, 1, 1), new DateTime(2026, 1, 2)));

        Assert.False(validation.IsValid);
    }

    [Fact]
    public void A_year_is_allowed()
    {
        var validation = new GetDesktopSalesAnalysisValidator()
            .Validate(new GetDesktopSalesAnalysisQuery(Guid.NewGuid(), new DateTime(2025, 1, 2), new DateTime(2026, 1, 2)));

        Assert.True(validation.IsValid);
    }

    // ---- Harness ----------------------------------------------------------------------------------

    private DesktopSaleEntity Sale(
        string externalReference,
        string? tender,
        decimal total,
        DateTime day,
        int utcHour,
        decimal? paid = null,
        string? paymentReference = null,
        string currency = "USD",
        string warehouse = "KEFSHOP",
        string source = SaleSourceSystems.ShopTill,
        Guid? createdBy = null,
        string cardCode = FarmPartner,
        string? cardName = FarmPartnerName,
        (string Code, int Quantity, decimal Net)[]? lines = null) => new()
        {
            ExternalReferenceId = externalReference,
            SourceSystem = source,
            CardCode = cardCode,
            CardName = cardName,
            WarehouseCode = warehouse,
            DocDate = day,
            TotalAmount = total,
            VatAmount = Math.Round(total * 0.13m, 2),
            AmountPaid = paid ?? total,
            Currency = currency,
            PaymentMethod = tender,
            PaymentReference = paymentReference,
            CreatedBy = (createdBy ?? _cashierId).ToString(),
            CreatedAt = DateTime.SpecifyKind(day.AddHours(utcHour).AddMinutes(15), DateTimeKind.Utc),
            Lines = (lines ?? [])
                .Select((line, index) => new DesktopSaleLineEntity
                {
                    LineNum = index + 1,
                    ItemCode = line.Code,
                    ItemDescription = line.Code,
                    Quantity = line.Quantity,
                    UnitPrice = Math.Round(line.Net / line.Quantity, 2),
                    LineTotal = line.Net,
                    WarehouseCode = warehouse,
                })
                .ToList(),
        };

    private static DesktopSalesCurrencyAnalysis Dollars(ErrorOr.ErrorOr<DesktopSalesAnalysisResult> result)
    {
        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return Dollars(result.Value);
    }

    private static DesktopSalesCurrencyAnalysis Dollars(DesktopSalesAnalysisResult result) =>
        result.Currencies.Single(c => c.Currency == "USD");

    private static DesktopSalesPaymentMethodRow Method(DesktopSalesCurrencyAnalysis currency, string method) =>
        currency.ByPaymentMethod.Single(row => row.PaymentMethod == method);

    private static void AssertMethod(DesktopSalesCurrencyAnalysis currency, string method, int count, decimal total)
    {
        var row = Method(currency, method);
        Assert.Equal(count, row.SalesCount);
        Assert.Equal(total, row.TotalAmount);
    }

    private async Task<Guid> AddUser(string role, int? shopId)
    {
        var id = Guid.NewGuid();
        _context.Users.Add(new User
        {
            Id = id,
            Username = $"u{id:N}"[..12],
            PasswordHash = "x",
            Role = role,
            IsActive = true,
            ShopId = shopId,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return id;
    }

    private Task<ErrorOr.ErrorOr<DesktopSalesAnalysisResult>> AnalyseAsAdmin() => Analyse(_adminId, Day1, Day2);

    private Task<ErrorOr.ErrorOr<DesktopSalesAnalysisResult>> Analyse(
        Guid callerId, DateTime from, DateTime to, string? warehouseCode = null, string? paymentMethod = null,
        string? sourceSystem = null) =>
        new GetDesktopSalesAnalysisHandler(_context, new RecordingAuditService())
            .Handle(
                new GetDesktopSalesAnalysisQuery(callerId, from, to, warehouseCode, SourceSystem: sourceSystem, PaymentMethod: paymentMethod),
                CancellationToken.None);
}
