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

    /// <summary>
    /// What has been credited back on the day's sales, tax-inclusive like <see cref="TotalSalesAmount"/>.
    /// </summary>
    /// <remarks>
    /// Counted against the day of the sale, not the day the credit was raised, so a report for a past day
    /// changes when one of its sales is credited. <see cref="TotalSalesAmount"/> does not: it stays what
    /// was rung up.
    /// </remarks>
    public decimal TotalCreditedAmount { get; set; }

    /// <summary><see cref="TotalSalesAmount"/> less <see cref="TotalCreditedAmount"/>: what the day's sales stand at.</summary>
    public decimal TotalNetOfCredits => TotalSalesAmount - TotalCreditedAmount;

    /// <summary>How many of the day's sales have had anything credited back.</summary>
    public int CreditedSalesCount { get; set; }

    /// <summary>How many of those were credited back in full.</summary>
    public int FullyCreditedSalesCount { get; set; }

    public int PostedInvoiceCount { get; set; }
    public int UnpostedInvoiceCount { get; set; }
    public List<BPSummaryDto> BusinessPartnerSummaries { get; set; } = new();
    public List<UnpostedSaleDto> UnpostedSales { get; set; } = new();

    /// <summary>
    /// SAP's booked gross profit on the posted sales here — by their own invoices, or the day's
    /// consolidated invoice; null when none is costed yet.
    /// </summary>
    public decimal? GrossProfit { get; set; }

    /// <summary>The value before VAT <see cref="GrossProfit"/> was measured on.</summary>
    public decimal? CostedNetAmount { get; set; }

    /// <summary>How many of the sales <see cref="GrossProfit"/> was measured on.</summary>
    public int CostedSalesCount { get; set; }

    /// <summary><see cref="GrossProfit"/> as a percentage of <see cref="CostedNetAmount"/>.</summary>
    public decimal? MarginPercent => GrossProfit is { } profit && CostedNetAmount is { } costed && costed != 0
        ? decimal.Round(profit / costed * 100m, 1, MidpointRounding.AwayFromZero)
        : null;

    /// <summary>Whether SAP's booked gross profit could be read, and what it means.</summary>
    public ShopInventory.Features.DesktopIntegration.Queries.GetManagementSalesReport.ManagementMarginStatus? Margin { get; set; }
}

public class BPSummaryDto
{
    public string CardCode { get; set; } = string.Empty;
    public string? CardName { get; set; }
    public int SalesCount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal TotalVat { get; set; }
    public decimal TotalPaid { get; set; }

    /// <summary>What has been credited back on this customer's sales of the day, tax-inclusive.</summary>
    public decimal CreditedAmount { get; set; }

    /// <summary><see cref="TotalAmount"/> less <see cref="CreditedAmount"/>.</summary>
    public decimal NetOfCredits => TotalAmount - CreditedAmount;

    public ConsolidatedInvoiceDto? ConsolidatedInvoice { get; set; }
    public ConsolidatedPaymentDto? IncomingPayment { get; set; }
    public List<IndividualSaleDto> IndividualSales { get; set; } = new();

    /// <summary>
    /// SAP's booked gross profit on the posted sales here — by their own invoices, or the day's
    /// consolidated invoice; null when none is costed yet.
    /// </summary>
    public decimal? GrossProfit { get; set; }

    /// <summary>The value before VAT <see cref="GrossProfit"/> was measured on.</summary>
    public decimal? CostedNetAmount { get; set; }

    /// <summary>How many of the sales <see cref="GrossProfit"/> was measured on.</summary>
    public int CostedSalesCount { get; set; }

    /// <summary><see cref="GrossProfit"/> as a percentage of <see cref="CostedNetAmount"/>.</summary>
    public decimal? MarginPercent => GrossProfit is { } profit && CostedNetAmount is { } costed && costed != 0
        ? decimal.Round(profit / costed * 100m, 1, MidpointRounding.AwayFromZero)
        : null;
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

    /// <summary>How much of <see cref="Amount"/> has been credited back, tax-inclusive; 0 when none has.</summary>
    public decimal CreditedAmount { get; set; }

    /// <summary>The numbers of the credits behind <see cref="CreditedAmount"/>; empty when none.</summary>
    public List<string> CreditNoteNumbers { get; set; } = new();

    /// <summary>
    /// SAP's booked gross profit on the posted sales here — by their own invoices, or the day's
    /// consolidated invoice; null when none is costed yet.
    /// </summary>
    public decimal? GrossProfit { get; set; }

    /// <summary>The value before VAT <see cref="GrossProfit"/> was measured on.</summary>
    public decimal? CostedNetAmount { get; set; }

    /// <summary>Whether this sale (1) or not (0) <see cref="GrossProfit"/> was measured on.</summary>
    public int CostedSalesCount { get; set; }

    /// <summary><see cref="GrossProfit"/> as a percentage of <see cref="CostedNetAmount"/>.</summary>
    public decimal? MarginPercent => GrossProfit is { } profit && CostedNetAmount is { } costed && costed != 0
        ? decimal.Round(profit / costed * 100m, 1, MidpointRounding.AwayFromZero)
        : null;
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
