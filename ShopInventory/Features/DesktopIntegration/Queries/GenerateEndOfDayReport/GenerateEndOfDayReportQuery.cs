using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Queries.GenerateEndOfDayReport;

/// <summary>
/// Generates the end-of-day sales report for a given date.
/// </summary>
public sealed record GenerateEndOfDayReportQuery(
    DateTime? ReportDate = null
) : IRequest<ErrorOr<EndOfDayReportDto>>;

public class EndOfDayReportDto
{
    public DateTime ReportDate { get; set; }
    public DateTime GeneratedAt { get; set; }
    public int TotalSalesCount { get; set; }
    public decimal TotalSalesAmount { get; set; }
    public decimal TotalVatAmount { get; set; }
    public decimal TotalAmountPaid { get; set; }
    public int PostedInvoiceCount { get; set; }
    public int UnpostedInvoiceCount { get; set; }
    public List<BPSummaryDto> BusinessPartnerSummaries { get; set; } = new();
    public List<UnpostedSaleDto> UnpostedSales { get; set; } = new();
}

public class BPSummaryDto
{
    public string CardCode { get; set; } = string.Empty;
    public string? CardName { get; set; }
    public int SalesCount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal TotalVat { get; set; }
    public decimal TotalPaid { get; set; }
    public ConsolidatedInvoiceDto? ConsolidatedInvoice { get; set; }
    public ConsolidatedPaymentDto? IncomingPayment { get; set; }
    public List<IndividualSaleDto> IndividualSales { get; set; } = new();
}

public class ConsolidatedInvoiceDto
{
    public int? SapDocEntry { get; set; }
    public int? SapDocNum { get; set; }
    public DateTime? PostedAt { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class ConsolidatedPaymentDto
{
    public int? SapDocEntry { get; set; }
    public int? SapDocNum { get; set; }
    public decimal Amount { get; set; }
    public string? Status { get; set; }
}

public class IndividualSaleDto
{
    public int SaleId { get; set; }
    public string ExternalReferenceId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public decimal VatAmount { get; set; }
    public string? FiscalReceiptNumber { get; set; }
    public string FiscalizationStatus { get; set; } = string.Empty;
    public string? PaymentMethod { get; set; }
    public decimal AmountPaid { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class UnpostedSaleDto
{
    public int SaleId { get; set; }
    public string ExternalReferenceId { get; set; } = string.Empty;
    public string CardCode { get; set; } = string.Empty;
    public string? CardName { get; set; }
    public decimal Amount { get; set; }
    public string? FiscalReceiptNumber { get; set; }
    public string ConsolidationStatus { get; set; } = string.Empty;
    public string? Reason { get; set; }
}
