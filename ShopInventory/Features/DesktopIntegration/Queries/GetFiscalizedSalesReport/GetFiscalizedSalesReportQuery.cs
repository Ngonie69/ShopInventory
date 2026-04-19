using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetFiscalizedSalesReport;

/// <summary>
/// Report period: Daily (single day), Weekly (Mon–Sun), Monthly (1st–last), or Custom date range.
/// </summary>
public enum ReportPeriod
{
    Daily,
    Weekly,
    Monthly,
    Custom
}

/// <summary>
/// Returns a detailed report of all fiscalized sales with line items, fiscal details,
/// and summary totals. Supports daily/weekly/monthly periods plus custom date ranges,
/// and filtering by customer, warehouse, and consolidation status.
/// </summary>
public sealed record GetFiscalizedSalesReportQuery(
    ReportPeriod Period = ReportPeriod.Daily,
    DateTime? Date = null,
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    string? CardCode = null,
    string? WarehouseCode = null,
    bool? IsConsolidated = null,
    int Page = 1,
    int PageSize = 50
) : IRequest<ErrorOr<FiscalizedSalesReportResult>>;

// ── Top-level report result ────────────────────────────────────────

public sealed record FiscalizedSalesReportResult(
    DateTime GeneratedAtUtc,
    string Period,
    DateTime FromDate,
    DateTime ToDate,
    FiscalizedSalesReportSummary Summary,
    List<DailyBreakdownDto> DailyBreakdown,
    List<FiscalizedSaleDto> Sales,
    int TotalCount,
    int Page,
    int PageSize,
    bool HasMore
);

// ── Aggregate summary ──────────────────────────────────────────────

public class FiscalizedSalesReportSummary
{
    public int TotalFiscalizedSales { get; set; }
    public int ConsolidatedCount { get; set; }
    public int AwaitingConsolidationCount { get; set; }
    public decimal TotalSalesAmount { get; set; }
    public decimal TotalVatAmount { get; set; }
    public int UniqueCustomers { get; set; }
    public int UniqueWarehouses { get; set; }
    public Dictionary<string, decimal> AmountByWarehouse { get; set; } = new();
    public Dictionary<string, decimal> AmountByCustomer { get; set; } = new();
}

// ── Daily breakdown (for weekly/monthly reports) ───────────────────

public class DailyBreakdownDto
{
    public DateTime Date { get; set; }
    public int SalesCount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal VatAmount { get; set; }
    public int ConsolidatedCount { get; set; }
    public int AwaitingConsolidationCount { get; set; }
}

// ── Single fiscalized sale ─────────────────────────────────────────

public class FiscalizedSaleDto
{
    public int QueueId { get; set; }
    public string ExternalReference { get; set; } = string.Empty;
    public string ReservationId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;

    // Customer
    public string CustomerCode { get; set; } = string.Empty;
    public string? CustomerName { get; set; }

    // Amounts
    public decimal TotalAmount { get; set; }
    public decimal VatAmount { get; set; }
    public string Currency { get; set; } = string.Empty;

    // Warehouse
    public string? WarehouseCode { get; set; }

    // Fiscal details
    public string? FiscalDeviceNumber { get; set; }
    public string? FiscalReceiptNumber { get; set; }
    public bool? FiscalizationSuccess { get; set; }

    // SAP posting (populated after consolidation)
    public string? SapDocEntry { get; set; }
    public int? SapDocNum { get; set; }
    public bool IsConsolidated { get; set; }

    // Timestamps
    public DateTime CreatedAt { get; set; }
    public DateTime? FiscalizedAt { get; set; }
    public DateTime? ConsolidatedAt { get; set; }

    // Source
    public string SourceSystem { get; set; } = string.Empty;
    public string? CreatedBy { get; set; }
    public string? Notes { get; set; }

    // Line items (deserialized from payload)
    public List<FiscalizedSaleLineDto> Lines { get; set; } = new();
}

// ── Line item detail ───────────────────────────────────────────────

public class FiscalizedSaleLineDto
{
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    public decimal DiscountPercent { get; set; }
    public string? WarehouseCode { get; set; }
    public string? TaxCode { get; set; }
    public string? UoMCode { get; set; }
}
