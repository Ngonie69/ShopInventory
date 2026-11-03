using ClosedXML.Excel;
using ShopInventory.Web.Features.ReportProductGroups;
using ShopInventory.Web.Features.Reports.MonthOnMonth;
using ShopInventory.Web.Features.Reports.Queries.GetCounterMonthOnMonth;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The Web side of the month-on-month report: product groups, cash vs plastic and product lines built from
/// the API's raw figures, and the workbook written from them.
/// </summary>
public sealed class CounterMonthOnMonthReportTests
{
    private static readonly DateTime Sep = new(2026, 9, 1);
    private static readonly DateTime Oct = new(2026, 10, 1);
    private static readonly DateTime AfterOctober = new(2026, 11, 3);

    private static readonly Dictionary<string, MonthOnMonthItemInfo> Items = new()
    {
        ["COK007"] = new("Coca Cola Coke 300ml", 150),
        ["COK020"] = new("Coca Cola Coke 440ml", 150), // added to SAP in October: joins through its item group
        ["ICS024"] = new("5 Litre Shamiso Vanilla", 103),
        ["ICS003"] = new("5 Litre Shamiso Icecream", 103),
        ["CHE001"] = new("Gouda", 104),
    };

    private static readonly Dictionary<int, string> ItemGroups = new() { [150] = "Complimentary Products", [103] = "Ice Cream", [104] = "Cheese" };

    private static readonly Dictionary<string, string> Warehouses = new() { ["KEFBYC"] = "Kefalos Bulawayo Centre" };

    private static readonly List<ReportProductGroupDto> Groups =
    [
        new(1, "Complimentary lines", null, 1, true, Sep, null, null,
            [new(ReportProductGroupMemberKind.ItemGroup, "150", "Complimentary Products")]),
        new(2, "Shamiso new flavours", null, 2, true, Sep, null, null,
            [new(ReportProductGroupMemberKind.Item, "ICS024", null)]),
        new(3, "Retired range", null, 3, false, Sep, null, null,
            [new(ReportProductGroupMemberKind.Item, "CHE001", null)]),
    ];

    private static CounterMonthOnMonthData Data(params CounterItemRow[] extraItems) => new(
        Sep, Oct,
        [
            new("shops", "KEFSHOP", "Kefalos Factory Shop", new DateTime(2026, 9, 9), 15, 25),
            new("shops", "CORMACH2", "Machipisa shop", new DateTime(2026, 9, 16), 13, 31),
            new("vending", "KEFBYC", null, new DateTime(2026, 9, 17), 12, 30),
        ],
        [
            Tender(Sep, "shops", "KEFSHOP", "Cash", 10, 900m),
            Tender(Sep, "shops", "KEFSHOP", "Swipe", 2, 100m),
            Tender(Oct, "shops", "KEFSHOP", "Cash", 20, 1900m, credited: 50m),
            Tender(Oct, "shops", "KEFSHOP", "Swipe", 1, 100m),
            Tender(Oct, "shops", "CORMACH2", "Ecocash", 3, 30m),
            Tender(Oct, "shops", "CORMACH2", "Cash", 30, 270m),
            Tender(Oct, "shops", "CORMACH2", "Not recorded", 1, 10m),
            Tender(Sep, "vending", "KEFBYC", "Cash", 5, 500m),
            Tender(Oct, "vending", "KEFBYC", "Cash", 9, 900m),
        ],
        [
            Item(Sep, "shops", "KEFSHOP", "COK007", 10, 100m),
            Item(Oct, "shops", "KEFSHOP", "COK007", 5, 50m),
            Item(Oct, "shops", "KEFSHOP", "COK020", 6, 60m),
            Item(Sep, "shops", "KEFSHOP", "ICS024", 2, 200m),
            Item(Oct, "shops", "KEFSHOP", "ICS024", 4, 400m),
            Item(Sep, "shops", "KEFSHOP", "CHE001", 7, 700m),
            Item(Oct, "shops", "KEFSHOP", "CHE001", 9, 1490m),
            Item(Oct, "shops", "CORMACH2", "COK007", 3, 30m),
            Item(Oct, "shops", "CORMACH2", "UNKNOWN1", 1, 230m),
            Item(Sep, "vending", "KEFBYC", "ICS003", 10, 500m),
            Item(Oct, "vending", "KEFBYC", "ICS003", 18, 900m),
            .. extraItems
        ]);

    private static CounterTenderRow Tender(DateTime month, string business, string warehouse, string tender, int sales, decimal gross,
        string currency = "USD", decimal credited = 0m) =>
        new(month, business, warehouse, currency, tender, sales, gross, Math.Round(gross * 0.13m, 2), credited > 0 ? 1 : 0, credited);

    private static CounterItemRow Item(DateTime month, string business, string warehouse, string code, decimal qty, decimal value, string currency = "USD") =>
        new(month, business, warehouse, currency, code, code, qty, value, 0, 0);

    private static CounterMonthOnMonthReport Build(CounterMonthOnMonthData? data = null, decimal? zigRate = null, DateTime? today = null) =>
        CounterMonthOnMonthReportBuilder.Build(data ?? Data(), Groups, Items, ItemGroups, Warehouses, zigRate, today ?? AfterOctober);

    [Fact]
    public void An_item_group_member_brings_in_items_added_to_that_group_later()
    {
        var complimentary = Build().Groups.Single(g => g.Name == "Complimentary lines");
        var factory = complimentary.Places.Single(r => r.Place.WarehouseCode == "KEFSHOP");

        Assert.Equal(new MonthPair(100m, 110m), factory.Value); // COK007 + the new COK020
        Assert.Contains(complimentary.Items, i => i.ItemCode == "COK020" && i.ItemName == "Coca Cola Coke 440ml");
        Assert.Equal(new MonthPair(100m, 140m), complimentary.TotalValue);
    }

    [Fact]
    public void A_named_item_member_counts_only_that_item_and_shares_are_of_the_places_item_sales()
    {
        var shamiso = Build().Groups.Single(g => g.Name == "Shamiso new flavours");
        var factory = shamiso.Places.Single(r => r.Place.WarehouseCode == "KEFSHOP");

        Assert.Equal(new MonthPair(200m, 400m), factory.Value);
        Assert.Equal(200m / 1000m, factory.Share.Previous);
        Assert.Equal(400m / 2000m, factory.Share.Current);
        Assert.Equal(1m, factory.Value.ChangeFraction);
    }

    [Fact]
    public void A_retired_group_is_left_out()
    {
        Assert.DoesNotContain(Build().Groups, g => g.Name == "Retired range");
    }

    [Fact]
    public void Every_shop_appears_in_a_group_even_with_nothing_sold_but_vending_only_when_it_sold()
    {
        var shamiso = Build().Groups.Single(g => g.Name == "Shamiso new flavours");

        Assert.Contains(shamiso.Places, r => r.Place.WarehouseCode == "CORMACH2" && r.Value == default);
        Assert.DoesNotContain(shamiso.Places, r => r.Place.Business == "vending");
    }

    [Fact]
    public void Tenders_land_in_cash_plastic_mobile_or_other()
    {
        var report = Build();
        var factory = report.Tenders.Single(t => t.Place.WarehouseCode == "KEFSHOP");
        var machipisa = report.Tenders.Single(t => t.Place.WarehouseCode == "CORMACH2");

        Assert.Equal(new MonthPair(900m, 1900m), factory.Cash);
        Assert.Equal(new MonthPair(100m, 100m), factory.Plastic);
        Assert.Equal(0.1m, factory.PlasticShare.Previous);
        Assert.Equal(0.05m, factory.PlasticShare.Current);
        Assert.Equal(new MonthPair(0, 50m), factory.Credits);
        Assert.Equal(new MonthPair(0, 30m), machipisa.Mobile);
        Assert.Equal(new MonthPair(0, 10m), machipisa.Other);
        Assert.Equal(["Not recorded"], report.OtherTenders);
    }

    [Fact]
    public void Product_lines_are_the_sap_item_group_within_each_business_and_place()
    {
        var lines = Build().ProductLines;

        var shopsCheese = lines.Single(l => l.Business == "shops" && l.Place is null && l.ProductLine == "Cheese");
        Assert.Equal(new MonthPair(700m, 1490m), shopsCheese.Value);
        Assert.Contains(lines, l => l.Place?.WarehouseCode == "CORMACH2" && l.ProductLine == CounterMonthOnMonthReportBuilder.NotInProductMaster);
        Assert.Equal(["Ice Cream"], lines.Where(l => l.Business == "vending" && l.Place is null).Select(l => l.ProductLine));
        Assert.Equal(1m, lines.Single(l => l.Business == "vending" && l.Place is null).Share.Current);
    }

    [Fact]
    public void A_vending_point_is_named_from_the_warehouse_cache()
    {
        Assert.Contains(Build().Places, p => p.WarehouseCode == "KEFBYC" && p.Name == "Kefalos Bulawayo Centre");
    }

    [Fact]
    public void A_place_that_started_on_the_till_mid_month_is_called_out()
    {
        var notes = Build().Notes;

        Assert.Contains(notes, n => n.Contains("Kefalos Factory Shop") && n.Contains("09 Sep 2026") && n.Contains("September"));
        Assert.DoesNotContain(notes, n => n.Contains("not over"));
    }

    [Fact]
    public void A_month_still_running_is_called_out()
    {
        var notes = Build(today: new DateTime(2026, 10, 20)).Notes;

        Assert.Contains(notes, n => n.Contains("October 2026 is not over yet") && n.Contains("20 Oct 2026"));
    }

    [Fact]
    public void ZiG_is_shown_in_usd_at_the_rate_given_and_left_out_without_one()
    {
        var data = Data(Item(Oct, "shops", "KEFSHOP", "ICS024", 1, 3000m, currency: "ZWG"));
        Assert.True(CounterMonthOnMonthReportBuilder.NeedsZigRate(data));

        var converted = Build(data, zigRate: 30m).Groups.Single(g => g.Name == "Shamiso new flavours");
        Assert.Equal(500m, converted.Places.Single(r => r.Place.WarehouseCode == "KEFSHOP").Value.Current);

        var withoutRate = Build(data);
        Assert.Equal(400m, withoutRate.Groups.Single(g => g.Name == "Shamiso new flavours").TotalValue.Current);
        Assert.Contains(withoutRate.Notes, n => n.Contains("ZWG"));
    }

    [Fact]
    public void The_workbook_has_a_sheet_per_product_group_and_its_totals_follow_the_rows()
    {
        var bytes = new ReportExportService().ExportCounterMonthOnMonthToExcel(Build());

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        Assert.Equal(
            ["Read me", "Summary", "Complimentary lines", "Shamiso new flavours", "Cash vs Plastic", "Product Lines"],
            workbook.Worksheets.Select(w => w.Name));

        var sheet = workbook.Worksheet("Complimentary lines");
        var header = sheet.CellsUsed().First(c => c.GetString() == "Value Oct 2026");
        var total = sheet.Column(header.Address.ColumnNumber).CellsUsed()
            .First(c => c.Address.RowNumber > header.Address.RowNumber && c.HasFormula);
        Assert.Equal(140d, total.GetDouble(), 2);

        // A sheet with two tables is set up for printing once: twice would print the letterhead twice.
        foreach (var name in new[] { "Complimentary lines", "Cash vs Plastic", "Summary" })
        {
            var letterhead = workbook.Worksheet(name).PageSetup.Header.Left.GetText(XLHFOccurrence.OddPages);
            Assert.Equal(1, letterhead.Split("KEFALOS", StringSplitOptions.None).Length - 1);
        }

        var readMe = workbook.Worksheet("Read me");
        Assert.Contains(readMe.CellsUsed(), c => c.GetString().Contains("first sold on the till on 09 Sep 2026"));
    }

    [Fact]
    public void A_group_name_excel_cannot_use_as_a_sheet_name_is_cleaned_and_kept_unique()
    {
        var groups = new List<ReportProductGroupDto>
        {
            new(1, "Ice cream: 5L / 2L [new]", null, 1, true, Sep, null, null, [new(ReportProductGroupMemberKind.Item, "ICS024", null)]),
            new(2, "Ice cream- 5L - 2L -new-", null, 2, true, Sep, null, null, [new(ReportProductGroupMemberKind.Item, "ICS003", null)]),
        };
        var report = CounterMonthOnMonthReportBuilder.Build(Data(), groups, Items, ItemGroups, Warehouses, null, AfterOctober);

        using var workbook = new XLWorkbook(new MemoryStream(new ReportExportService().ExportCounterMonthOnMonthToExcel(report)));

        Assert.Contains(workbook.Worksheets, w => w.Name == "Ice cream- 5L - 2L -new-");
        Assert.Contains(workbook.Worksheets, w => w.Name == "Ice cream- 5L - 2L -new- (2)");
    }
}
