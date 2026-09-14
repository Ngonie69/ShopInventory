namespace ShopInventory.Web.Features.Reports.Queries.GetManagementSalesReport;

// The web copies of the API's GetManagementSalesReport result records, property for property.

public sealed class ManagementSalesReportResult
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public DateTime PreviousFromDate { get; set; }
    public DateTime PreviousToDate { get; set; }
    public string? WarehouseCode { get; set; }
    public string? SourceSystem { get; set; }
    public DateTime GeneratedAtUtc { get; set; }
    public List<ManagementCurrencySection> Currencies { get; set; } = [];
    public ManagementSalesHealth Health { get; set; } = new();
    public ManagementMarginStatus Margin { get; set; } = new();
}

public sealed class ManagementCurrencySection
{
    public string Currency { get; set; } = string.Empty;
    public ManagementSummary Summary { get; set; } = new();
    public List<ManagementDayRow> ByDay { get; set; } = [];
    public List<ManagementBreakdownRow> ByChannel { get; set; } = [];
    public List<ManagementBreakdownRow> ByDepot { get; set; } = [];
    public List<ManagementBreakdownRow> ByCostCentre { get; set; } = [];
    public List<ManagementBreakdownRow> ByOperator { get; set; } = [];
    public List<ManagementBreakdownRow> ByPaymentMethod { get; set; } = [];
    public List<ManagementBreakdownRow> ByVendor { get; set; } = [];
    public List<ManagementLapsedVendorRow> LapsedVendors { get; set; } = [];
    public List<ManagementItemRow> ByItem { get; set; } = [];
    public List<ManagementItemGroupRow> ByItemGroup { get; set; } = [];
    public List<ManagementItemDepotCell> ItemDepotMatrix { get; set; } = [];
}

public sealed class ManagementSummary
{
    public int SalesCount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal VatAmount { get; set; }
    public decimal NetAmount { get; set; }
    public decimal AverageSale { get; set; }
    public decimal QuantitySold { get; set; }
    public int DaysTraded { get; set; }
    public int VendorsServed { get; set; }
    public int PreviousSalesCount { get; set; }
    public decimal PreviousTotalAmount { get; set; }
    public decimal PreviousNetAmount { get; set; }
    public decimal PreviousAverageSale { get; set; }
    public int PreviousVendorsServed { get; set; }
    public decimal? TotalChangePercent { get; set; }
    public decimal? SalesCountChangePercent { get; set; }
    public decimal? AverageSaleChangePercent { get; set; }

    /// <summary>SAP's booked gross profit on the sales it holds cost for; null when none.</summary>
    public decimal? GrossProfit { get; set; }

    /// <summary>Over the revenue SAP costed, not over all takings.</summary>
    public decimal? MarginPercent { get; set; }

    public decimal CostedNetAmount { get; set; }
    public int CostedSalesCount { get; set; }
}

public sealed class ManagementDayRow
{
    public DateTime Date { get; set; }
    public int SalesCount { get; set; }
    public decimal TotalAmount { get; set; }
    public DateTime ComparedDate { get; set; }
    public int ComparedSalesCount { get; set; }
    public decimal ComparedTotalAmount { get; set; }
}

public sealed class ManagementBreakdownRow
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Hint { get; set; }
    public int SalesCount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal NetAmount { get; set; }
    public decimal ShareOfValuePercent { get; set; }
    public int PreviousSalesCount { get; set; }
    public decimal PreviousTotalAmount { get; set; }
    public decimal? ChangePercent { get; set; }
    public decimal? GrossProfit { get; set; }
    public decimal? MarginPercent { get; set; }
}

public sealed class ManagementLapsedVendorRow
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Hint { get; set; }
    public int PreviousSalesCount { get; set; }
    public decimal PreviousTotalAmount { get; set; }
    public DateTime LastSaleDate { get; set; }
}

public sealed class ManagementItemRow
{
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public int? ItemsGroupCode { get; set; }
    public decimal Quantity { get; set; }
    public decimal NetAmount { get; set; }
    public int SalesCount { get; set; }
    public decimal ShareOfNetPercent { get; set; }
    public decimal PreviousQuantity { get; set; }
    public decimal PreviousNetAmount { get; set; }
    public decimal? ChangePercent { get; set; }
    public decimal? QuantityChangePercent { get; set; }
    public decimal AverageUnitPrice { get; set; }
    public decimal PreviousAverageUnitPrice { get; set; }
    public decimal? PriceChangePercent { get; set; }
    public decimal UnitsPerSale { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal? GrossProfit { get; set; }
    public decimal? MarginPercent { get; set; }
}

public sealed class ManagementItemGroupRow
{
    public int? ItemsGroupCode { get; set; }
    public int ItemCount { get; set; }
    public decimal Quantity { get; set; }
    public decimal NetAmount { get; set; }
    public decimal ShareOfNetPercent { get; set; }
    public decimal PreviousQuantity { get; set; }
    public decimal PreviousNetAmount { get; set; }
    public decimal? ChangePercent { get; set; }
    public decimal? GrossProfit { get; set; }
    public decimal? MarginPercent { get; set; }
}

public sealed class ManagementItemDepotCell
{
    public string ItemCode { get; set; } = string.Empty;
    public string WarehouseCode { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal NetAmount { get; set; }
}

public sealed class ManagementHealthBucket
{
    public int SalesCount { get; set; }
    public List<ManagementCurrencyAmount> Value { get; set; } = [];
}

public sealed class ManagementCurrencyAmount
{
    public string Currency { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

public sealed class ManagementSalesHealth
{
    public int SalesCount { get; set; }
    public ManagementHealthBucket FiscalSucceeded { get; set; } = new();
    public ManagementHealthBucket FiscalPending { get; set; } = new();
    public ManagementHealthBucket FiscalFailed { get; set; } = new();
    public ManagementHealthBucket FiscalSkipped { get; set; } = new();
    public ManagementHealthBucket NeedsReconciliation { get; set; } = new();
    public int PostingSalesCount { get; set; }
    public ManagementHealthBucket Posted { get; set; } = new();
    public ManagementHealthBucket PostingWaiting { get; set; } = new();
    public ManagementHealthBucket PostingFailing { get; set; } = new();
    public ManagementHealthBucket PaymentFailed { get; set; } = new();
    public DateTime? OldestUnpostedDate { get; set; }
    public string? LatestPostingError { get; set; }
}

public sealed class ManagementMarginStatus
{
    public bool Available { get; set; }
    public string Detail { get; set; } = string.Empty;
}
