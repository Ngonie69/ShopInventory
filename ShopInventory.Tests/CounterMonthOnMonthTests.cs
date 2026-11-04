using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetCounterMonthOnMonth;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// The data behind the Shop &amp; Vending month-on-month report: two months by place, tender and item.
/// </summary>
public sealed class CounterMonthOnMonthTests : IDisposable
{
    private static readonly DateTime September = new(2026, 9, 1);
    private static readonly DateTime October = new(2026, 10, 1);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly Guid _adminId = Guid.NewGuid();
    private readonly int _farmId;

    public CounterMonthOnMonthTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        var farm = new ShopEntity { Code = "FARM", Name = "Kefalos Factory Shop", BusinessPartnerCode = "CIS006", WarehouseCode = "KEFSHOP", IsActive = true };
        var machipisa = new ShopEntity { Code = "MACH", Name = "Machipisa shop", BusinessPartnerCode = "MAC009", WarehouseCode = "CORMACH2", IsActive = true };
        _context.Shops.AddRange(farm, machipisa);
        _context.SaveChanges();
        _farmId = farm.Id;

        _context.Users.Add(new User { Id = _adminId, Username = "admin", PasswordHash = "x", Role = ApplicationRoles.Admin, IsActive = true });

        _context.DesktopSales.AddRange(
            // Factory shop: opened on 9 September, then a full October.
            Sale("S1", "KEFSHOP", new DateTime(2026, 9, 9), "Cash", 23m, ("ICS024", 2, 20m)),
            Sale("S2", "KEFSHOP", new DateTime(2026, 9, 20), "swipe", 11.5m, ("COK007", 10, 10m)),
            Sale("S3", "KEFSHOP", new DateTime(2026, 10, 2), "cash", 46m, ("ICS024", 4, 40m)),
            Sale("S4", "KEFSHOP", new DateTime(2026, 10, 2), "Cash", 11.5m, ("COK007", 5, 5m), ("ics025", 1, 5m)),
            Sale("S5", "KEFSHOP", new DateTime(2026, 10, 3), "Swipe", 34.5m, ("ICS003", 3, 30m)),
            // Machipisa shop: October only.
            Sale("S6", "CORMACH2", new DateTime(2026, 10, 5), "Ecocash", 5.75m, ("COK007", 5, 5m)),
            // Vending: cash, its own business.
            Sale("V1", "KEFBYC", new DateTime(2026, 9, 17), "Cash", 115m, ("ICS003", 10, 100m), source: SaleSourceSystems.Vending),
            Sale("V2", "KEFBYC", new DateTime(2026, 10, 17), "Cash", 230m, ("ICS003", 20, 200m), source: SaleSourceSystems.Vending),
            // A van sale is reported under van sales, never here.
            Sale("VAN1", "VAN010", new DateTime(2026, 10, 10), "Cash", 999m, ("ICS003", 99, 999m), source: SaleSourceSystems.VanSales),
            // August is outside both months.
            Sale("A1", "KEFSHOP", new DateTime(2026, 8, 30), "Cash", 50m, ("ICS024", 5, 50m)));
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private static DesktopSaleEntity Sale(
        string reference, string warehouse, DateTime day, string tender, decimal total,
        params (string Code, decimal Quantity, decimal Net)[] lines) =>
        Sale(reference, warehouse, day, tender, total, lines, SaleSourceSystems.ShopTill);

    private static DesktopSaleEntity Sale(
        string reference, string warehouse, DateTime day, string tender, decimal total,
        (string Code, decimal Quantity, decimal Net) line, string source) =>
        Sale(reference, warehouse, day, tender, total, [line], source);

    private static DesktopSaleEntity Sale(
        string reference, string warehouse, DateTime day, string tender, decimal total,
        (string Code, decimal Quantity, decimal Net)[] lines, string source) => new()
        {
            ExternalReferenceId = reference,
            SourceSystem = source,
            CardCode = "CASH",
            WarehouseCode = warehouse,
            DocDate = day,
            TotalAmount = total,
            VatAmount = Math.Round(total - lines.Sum(l => l.Net), 2),
            AmountPaid = total,
            Currency = "USD",
            PaymentMethod = tender,
            CreatedAt = DateTime.SpecifyKind(day.AddHours(8), DateTimeKind.Utc),
            Lines = lines.Select((l, i) => new DesktopSaleLineEntity
            {
                LineNum = i + 1,
                ItemCode = l.Code,
                ItemDescription = l.Code,
                Quantity = l.Quantity,
                UnitPrice = l.Net / l.Quantity,
                LineTotal = l.Net,
                WarehouseCode = warehouse
            }).ToList()
        };

    private async Task<CounterMonthOnMonthResult> Read(Guid? caller = null)
    {
        var result = await new GetCounterMonthOnMonthHandler(_context, new RecordingAuditService())
            .Handle(new GetCounterMonthOnMonthQuery(caller ?? _adminId, new DateTime(2026, 9, 14), new DateTime(2026, 10, 31)), default);
        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    [Fact]
    public async Task Months_are_read_from_their_first_day_whatever_day_was_passed()
    {
        var result = await Read();

        Assert.Equal(September, result.PreviousMonth);
        Assert.Equal(October, result.CurrentMonth);
    }

    [Fact]
    public async Task Every_shop_and_vending_point_is_a_place_but_no_van_is()
    {
        var places = (await Read()).Places;

        Assert.Equal(
            new[] { ("shops", "CORMACH2"), ("shops", "KEFSHOP"), ("vending", "KEFBYC") },
            places.Select(p => (p.Business, p.WarehouseCode)));
        Assert.Equal("Kefalos Factory Shop", places.Single(p => p.WarehouseCode == "KEFSHOP").ShopName);
        Assert.Null(places.Single(p => p.WarehouseCode == "KEFBYC").ShopName);
    }

    [Fact]
    public async Task A_vending_point_is_named_by_its_depots_business_partner_not_its_warehouse()
    {
        _context.Users.Add(new User
        {
            Username = "byo-cart",
            PasswordHash = "x",
            Role = ApplicationRoles.CartVendor,
            IsActive = true,
            AssignedBusinessPartnerCode = "COR008",
            AssignedWarehouseCodes = "[\"KEFBYC\"]"
        });
        var named = Sale("VEND-NAME", "KEFBYC", new DateTime(2026, 10, 20), "Cash", 5m, ("YOG145", 1m, 4.35m), SaleSourceSystems.Vending);
        named.CardCode = "COR008";
        named.CardName = "Cortina Bulawayo Vending";
        _context.DesktopSales.Add(named);
        await _context.SaveChangesAsync();

        var depot = (await Read()).Places.Single(p => p.WarehouseCode == "KEFBYC");

        Assert.Equal("vending", depot.Business);
        Assert.Equal("Cortina Bulawayo Vending", depot.ShopName);
    }

    [Fact]
    public async Task A_place_states_its_trading_days_and_the_first_day_it_ever_sold()
    {
        var farm = (await Read()).Places.Single(p => p.WarehouseCode == "KEFSHOP");

        Assert.Equal(2, farm.PreviousTradingDays);
        Assert.Equal(2, farm.CurrentTradingDays);
        // The August sale is outside both months but is still the first day the shop sold.
        Assert.Equal(new DateTime(2026, 8, 30), farm.FirstSaleDate);
    }

    [Fact]
    public async Task Tenders_fold_their_stored_spellings_and_stay_in_their_month()
    {
        var tenders = (await Read()).Tenders.Where(t => t.WarehouseCode == "KEFSHOP").ToList();

        var octoberCash = tenders.Single(t => t.Month == October && t.Tender == TenderTypes.Cash);
        Assert.Equal(2, octoberCash.Sales);
        Assert.Equal(57.5m, octoberCash.Gross);
        Assert.Equal(7.5m, octoberCash.Vat);

        Assert.Equal(11.5m, tenders.Single(t => t.Month == September && t.Tender == TenderTypes.Swipe).Gross);
        Assert.Equal(34.5m, tenders.Single(t => t.Month == October && t.Tender == TenderTypes.Swipe).Gross);
        Assert.DoesNotContain(tenders, t => t.Tender is "cash" or "swipe");
    }

    [Fact]
    public async Task Items_are_per_month_place_and_upper_cased_code_before_vat()
    {
        var items = (await Read()).Items;

        var octoberVanilla = items.Single(i => i.Month == October && i.WarehouseCode == "KEFSHOP" && i.ItemCode == "ICS024");
        Assert.Equal(4m, octoberVanilla.Quantity);
        Assert.Equal(40m, octoberVanilla.NetValue);
        Assert.Contains(items, i => i.ItemCode == "ICS025");
        Assert.Equal(300m, items.Where(i => i.Business == SaleBusinesses.Vending).Sum(i => i.NetValue));
        Assert.DoesNotContain(items, i => i.WarehouseCode == "VAN010");
        Assert.DoesNotContain(items, i => i.Quantity == 5m && i.ItemCode == "ICS024"); // August
    }

    [Fact]
    public async Task A_credit_comes_off_the_item_and_is_stated_on_its_tender()
    {
        var saleId = await _context.DesktopSales.Where(s => s.ExternalReferenceId == "S3").Select(s => s.Id).SingleAsync();
        await SaleCreditSeed.TillCreditAsync(_context, saleId, 11.5m, lines: (ReceiptLine: 1, Quantity: 1m, UnitPrice: 10m));

        var result = await Read();

        var vanilla = result.Items.Single(i => i.Month == October && i.WarehouseCode == "KEFSHOP" && i.ItemCode == "ICS024");
        Assert.Equal(1m, vanilla.CreditedQuantity);
        Assert.Equal(3m, vanilla.Quantity);
        Assert.Equal(40m - vanilla.CreditedNetValue, vanilla.NetValue);
        Assert.True(vanilla.CreditedNetValue > 0);

        var cash = result.Tenders.Single(t => t.Month == October && t.WarehouseCode == "KEFSHOP" && t.Tender == TenderTypes.Cash);
        Assert.Equal(1, cash.CreditedSales);
        Assert.Equal(11.5m, cash.CreditedAmount);
        Assert.Equal(57.5m, cash.Gross); // what was sold stands; the credit is beside it
    }

    [Fact]
    public async Task A_cashier_tied_to_a_shop_reads_only_that_shop()
    {
        var cashierId = Guid.NewGuid();
        _context.Users.Add(new User { Id = cashierId, Username = "rudo", PasswordHash = "x", Role = ApplicationRoles.Cashier, IsActive = true, ShopId = _farmId });
        await _context.SaveChangesAsync();

        var result = await Read(cashierId);

        Assert.All(result.Places, p => Assert.Equal("KEFSHOP", p.WarehouseCode));
        Assert.All(result.Items, i => Assert.Equal("KEFSHOP", i.WarehouseCode));
    }

    [Fact]
    public void The_same_month_twice_is_refused()
    {
        var result = new GetCounterMonthOnMonthValidator().Validate(
            new GetCounterMonthOnMonthQuery(Guid.NewGuid(), new DateTime(2026, 10, 1), new DateTime(2026, 10, 20)));

        Assert.False(result.IsValid);
    }
}
