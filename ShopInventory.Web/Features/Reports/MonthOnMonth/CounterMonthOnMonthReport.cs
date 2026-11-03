namespace ShopInventory.Web.Features.Reports.MonthOnMonth;

/// <summary>A figure in each of the two months compared.</summary>
public readonly record struct MonthPair(decimal Previous, decimal Current)
{
    public decimal Change => Current - Previous;

    /// <summary>Null when there is nothing to compare against: the earlier month was zero or negative.</summary>
    public decimal? ChangeFraction => Previous > 0 ? Current / Previous - 1 : null;

    /// <summary>True when the earlier month had nothing and the later one does.</summary>
    public bool IsNew => Previous == 0 && Current != 0;

    public static MonthPair operator +(MonthPair a, MonthPair b) => new(a.Previous + b.Previous, a.Current + b.Current);
}

/// <summary>A shop or vending point, keyed by its business and warehouse.</summary>
public sealed record MonthOnMonthPlace(
    string Business,
    string WarehouseCode,
    string Name,
    DateTime? FirstSaleDate,
    int PreviousTradingDays,
    int CurrentTradingDays,
    MonthPair Takings,
    MonthPair Credits,
    MonthPair ItemValue)
{
    public string Key => $"{Business}|{WarehouseCode}";

    public string BusinessLabel => Business == "vending" ? "Vending" : "Shops";

    public MonthPair NetTakings => new(Takings.Previous - Credits.Previous, Takings.Current - Credits.Current);
}

/// <summary>A product group's figures at one place, with its share of that place's item sales.</summary>
public sealed record MonthOnMonthGroupPlaceRow(MonthOnMonthPlace Place, MonthPair Quantity, MonthPair Value, MonthPair Share);

/// <summary>One item of a product group at one place.</summary>
public sealed record MonthOnMonthGroupItemRow(MonthOnMonthPlace Place, string ItemCode, string ItemName, MonthPair Quantity, MonthPair Value);

public sealed record MonthOnMonthGroupSection(
    int GroupId,
    string Name,
    string? Description,
    string MembersSummary,
    IReadOnlyList<MonthOnMonthGroupPlaceRow> Places,
    IReadOnlyList<MonthOnMonthGroupItemRow> Items)
{
    public MonthPair TotalValue => Places.Aggregate(default(MonthPair), (sum, row) => sum + row.Value);

    public MonthPair TotalQuantity => Places.Aggregate(default(MonthPair), (sum, row) => sum + row.Quantity);
}

/// <summary>How a place's takings were paid, incl. VAT, with the number of sales of each.</summary>
public sealed record MonthOnMonthTenderRow(
    MonthOnMonthPlace Place,
    MonthPair Cash,
    MonthPair Plastic,
    MonthPair Mobile,
    MonthPair Other,
    MonthPair CashSales,
    MonthPair PlasticSales,
    MonthPair MobileSales,
    MonthPair OtherSales,
    MonthPair Credits)
{
    public MonthPair Total => Cash + Plastic + Mobile + Other;

    public MonthPair CashShare => Share(Cash, Total);

    public MonthPair PlasticShare => Share(Plastic, Total);

    public MonthPair MobileShare => Share(Mobile, Total);

    private static MonthPair Share(MonthPair part, MonthPair whole) => new(
        whole.Previous == 0 ? 0 : part.Previous / whole.Previous,
        whole.Current == 0 ? 0 : part.Current / whole.Current);
}

/// <summary>A product line (SAP item group) at one place, or across a business when Place is null.</summary>
public sealed record MonthOnMonthProductLineRow(
    string Business,
    MonthOnMonthPlace? Place,
    string ProductLine,
    MonthPair Quantity,
    MonthPair Value,
    MonthPair Share);

/// <summary>Everything the month-on-month page and workbook show, amounts in USD.</summary>
public sealed record CounterMonthOnMonthReport(
    DateTime PreviousMonth,
    DateTime CurrentMonth,
    decimal? ZigRate,
    IReadOnlyList<string> Notes,
    IReadOnlyList<MonthOnMonthPlace> Places,
    IReadOnlyList<MonthOnMonthGroupSection> Groups,
    IReadOnlyList<MonthOnMonthTenderRow> Tenders,
    IReadOnlyList<MonthOnMonthProductLineRow> ProductLines,
    IReadOnlyList<string> OtherTenders)
{
    public string PreviousLabel => PreviousMonth.ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    public string CurrentLabel => CurrentMonth.ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    public MonthPair TotalNetTakings => Places.Aggregate(default(MonthPair), (sum, p) => sum + p.NetTakings);
}
