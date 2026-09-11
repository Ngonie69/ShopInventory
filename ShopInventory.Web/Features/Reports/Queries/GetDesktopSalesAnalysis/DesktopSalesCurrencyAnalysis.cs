namespace ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesAnalysis;

/// <summary>Every figure the analysis states for the sales made in one currency.</summary>
public sealed class DesktopSalesCurrencyAnalysis
{
    public string Currency { get; set; } = string.Empty;

    public int SalesCount { get; set; }

    /// <summary>What the sales came to, VAT included.</summary>
    public decimal TotalAmount { get; set; }

    public decimal VatAmount { get; set; }

    public decimal NetAmount { get; set; }

    public decimal AmountPaid { get; set; }

    public decimal ChangeGiven { get; set; }

    public decimal AverageSale { get; set; }

    public decimal QuantitySold { get; set; }

    public int DaysTraded { get; set; }

    public int DistinctItems { get; set; }

    public List<DesktopSalesPaymentMethodRow> ByPaymentMethod { get; set; } = [];

    public List<DesktopSalesDayRow> ByDay { get; set; } = [];

    public List<DesktopSalesHourRow> ByHour { get; set; } = [];

    public List<DesktopSalesBreakdownRow> ByWarehouse { get; set; } = [];

    public List<DesktopSalesBreakdownRow> BySource { get; set; } = [];

    public List<DesktopSalesBreakdownRow> ByOperator { get; set; } = [];

    public List<DesktopSalesItemRow> TopItems { get; set; } = [];
}
