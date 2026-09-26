namespace ShopInventory.Web.Models;

// Hand-kept mirrors of ShopInventory/DTOs/CountVarianceDto.cs. Nullability matches the API's: a field
// the API may send as null is nullable here, or the page reads it as no data.

/// <summary>The line states <c>CountVarianceLineDto.Status</c> carries.</summary>
public static class CountVarianceLineStatus
{
    public const string Short = "Short";
    public const string Over = "Over";
    public const string Matched = "Matched";
    public const string NotCounted = "NotCounted";
}

public sealed class CountingDocumentSummary
{
    public int DocumentEntry { get; set; }
    public int DocumentNumber { get; set; }
    public DateTime? CountDate { get; set; }
    public string? CountTime { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Remarks { get; set; }
    public string? CounterName { get; set; }

    public bool IsOpen => string.Equals(Status, "Open", StringComparison.OrdinalIgnoreCase);
}

public sealed class CountingDocumentListResponse
{
    public List<CountingDocumentSummary> Documents { get; set; } = [];
}

public sealed class CountVarianceLine
{
    public int RowNumber { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public string? WarehouseCode { get; set; }
    public string? UoMCode { get; set; }
    public decimal InWarehouseQuantity { get; set; }
    public decimal? CountedQuantity { get; set; }
    public decimal Variance { get; set; }
    public decimal? SellingPrice { get; set; }
    public decimal? VarianceValue { get; set; }
    public string Status { get; set; } = string.Empty;
}

public sealed class CountVarianceTotals
{
    public int LineCount { get; set; }
    public int ShortLines { get; set; }
    public int OverLines { get; set; }
    public int MatchedLines { get; set; }
    public int NotCountedLines { get; set; }
    public int UnpricedLines { get; set; }
    public int UnvaluedVarianceLines { get; set; }
    public decimal ShortQuantity { get; set; }
    public decimal OverQuantity { get; set; }
    public decimal ShortValue { get; set; }
    public decimal OverValue { get; set; }
    public decimal NetValue { get; set; }
    public decimal StockValue { get; set; }
}

public sealed class CountVarianceReport
{
    public CountingDocumentSummary Document { get; set; } = new();
    public List<string> Warehouses { get; set; } = [];
    public int PriceListNum { get; set; }
    public string? PriceListName { get; set; }
    public string? Currency { get; set; }
    public List<CountVarianceLine> Lines { get; set; } = [];
    public CountVarianceTotals Totals { get; set; } = new();
    public DateTime GeneratedAtUtc { get; set; }
}
