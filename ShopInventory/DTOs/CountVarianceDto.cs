namespace ShopInventory.DTOs;

/// <summary>
/// Where a counted line stands against the books. Strings rather than an enum so the Web's hand-kept
/// mirror cannot fall out of step on a renumbering.
/// </summary>
public static class CountVarianceLineStatus
{
    public const string Short = "Short";
    public const string Over = "Over";
    public const string Matched = "Matched";

    /// <summary>Nobody has counted the line yet. SAP reports its variance as zero, which is not a match.</summary>
    public const string NotCounted = "NotCounted";
}

/// <summary>One SAP inventory count, as the picker lists it.</summary>
public sealed class CountingDocumentSummaryDto
{
    public int DocumentEntry { get; set; }
    public int DocumentNumber { get; set; }

    /// <summary>The count date. A calendar date: SAP holds no time zone for it.</summary>
    public DateTime? CountDate { get; set; }

    /// <summary><c>HH:mm</c>, as keyed into B1.</summary>
    public string? CountTime { get; set; }

    /// <summary><c>Open</c> or <c>Closed</c>.</summary>
    public string Status { get; set; } = string.Empty;

    public string? Remarks { get; set; }

    /// <summary>The single counter's name, when the count names one SAP user; null otherwise.</summary>
    public string? CounterName { get; set; }
}

public sealed class CountingDocumentListResponseDto
{
    public List<CountingDocumentSummaryDto> Documents { get; set; } = [];
}

/// <summary>One line of a count, valued at selling price.</summary>
public sealed class CountVarianceLineDto
{
    /// <summary>The row number B1 shows on the count, so a line can be found on screen there.</summary>
    public int RowNumber { get; set; }

    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public string? WarehouseCode { get; set; }
    public string? UoMCode { get; set; }
    public decimal InWarehouseQuantity { get; set; }

    /// <summary>Null until the line is counted.</summary>
    public decimal? CountedQuantity { get; set; }

    /// <summary>Counted minus in-warehouse, in inventory units; zero for an uncounted line.</summary>
    public decimal Variance { get; set; }

    /// <summary>The price on the selling price list, excluding VAT; null when the list has none for the item.</summary>
    public decimal? SellingPrice { get; set; }

    /// <summary><see cref="Variance"/> times <see cref="SellingPrice"/>; null when there is no price.</summary>
    public decimal? VarianceValue { get; set; }

    /// <summary>One of <see cref="CountVarianceLineStatus"/>.</summary>
    public string Status { get; set; } = string.Empty;
}

public sealed class CountVarianceTotalsDto
{
    public int LineCount { get; set; }
    public int ShortLines { get; set; }
    public int OverLines { get; set; }
    public int MatchedLines { get; set; }
    public int NotCountedLines { get; set; }

    /// <summary>Lines the price list has no price for, whatever their variance.</summary>
    public int UnpricedLines { get; set; }

    /// <summary>Lines with a variance that could not be valued, and so are missing from the money totals.</summary>
    public int UnvaluedVarianceLines { get; set; }

    /// <summary>Units short, as a positive number.</summary>
    public decimal ShortQuantity { get; set; }
    public decimal OverQuantity { get; set; }

    /// <summary>Negative or zero.</summary>
    public decimal ShortValue { get; set; }

    /// <summary>Positive or zero.</summary>
    public decimal OverValue { get; set; }

    public decimal NetValue { get; set; }

    /// <summary>The in-warehouse quantity of every priced line, at selling price: what the variance is measured against.</summary>
    public decimal StockValue { get; set; }
}

/// <summary>One van's latest count in the range, valued.</summary>
public sealed class VanCountVarianceDto
{
    public string WarehouseCode { get; set; } = string.Empty;

    /// <summary>The rep assigned to the van; null when the assignment names nobody by name.</summary>
    public string? RepName { get; set; }

    public CountingDocumentSummaryDto Document { get; set; } = new();

    /// <summary>This van's lines only — a count spanning several warehouses is split between them.</summary>
    public CountVarianceTotalsDto Totals { get; set; } = new();

    /// <summary>Older counts of this van in the range, which the latest one replaces.</summary>
    public List<int> SupersededDocumentNumbers { get; set; } = [];
}

/// <summary>A van with no count dated in the range.</summary>
public sealed class VanWithoutCountDto
{
    public string WarehouseCode { get; set; } = string.Empty;
    public string? RepName { get; set; }
}

/// <summary>One item across every van counted.</summary>
public sealed class ItemCountVarianceDto
{
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }

    /// <summary>Null when the price list has no price for the item.</summary>
    public decimal? SellingPrice { get; set; }

    /// <summary>Vans that counted the item short, and over.</summary>
    public int VansShort { get; set; }
    public int VansOver { get; set; }

    /// <summary>Units short across the vans, as a positive number.</summary>
    public decimal ShortQuantity { get; set; }
    public decimal OverQuantity { get; set; }
    public decimal NetQuantity { get; set; }

    /// <summary>Null when there is no price.</summary>
    public decimal? NetValue { get; set; }
}

/// <summary>Every van's latest count in a date range, valued at selling price and added up.</summary>
public sealed class VanCountVarianceReportDto
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }

    public int PriceListNum { get; set; }
    public string? PriceListName { get; set; }
    public string? Currency { get; set; }

    /// <summary>How many vans the application knows of.</summary>
    public int VanCount { get; set; }

    public List<VanCountVarianceDto> Vans { get; set; } = [];
    public List<VanWithoutCountDto> VansNotCounted { get; set; } = [];
    public List<ItemCountVarianceDto> Items { get; set; } = [];

    /// <summary>The vans' totals added together.</summary>
    public CountVarianceTotalsDto Totals { get; set; } = new();

    /// <summary>
    /// True when SAP held more counts in the range than one run reads, so a van's latest count may
    /// have been missed. Narrow the range.
    /// </summary>
    public bool Truncated { get; set; }

    public DateTime GeneratedAtUtc { get; set; }
}

public sealed class CountVarianceReportDto
{
    public CountingDocumentSummaryDto Document { get; set; } = new();

    /// <summary>Every warehouse the count's lines are in; usually one.</summary>
    public List<string> Warehouses { get; set; } = [];

    public int PriceListNum { get; set; }
    public string? PriceListName { get; set; }
    public string? Currency { get; set; }

    public List<CountVarianceLineDto> Lines { get; set; } = [];
    public CountVarianceTotalsDto Totals { get; set; } = new();

    /// <summary>When SAP was read for this report, in UTC.</summary>
    public DateTime GeneratedAtUtc { get; set; }
}
