using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSales;

/// <summary>
/// A page of captured sales.
/// </summary>
/// <remarks>
/// <c>SourceSystem</c> selects exactly one source, or null for the default scope. Null is not
/// "everything": it excludes <c>SaleSourceSystems.VanSalesOnline</c>, whose rows are receipt carriers for
/// sales already recorded as their confirmed reservation and their SAP invoice. Name that source to list
/// them.
///
/// <c>CallerUserId</c> is required and positional rather than an optional trailing parameter, because
/// it is what the handler resolves the warehouse scope from. Before it existed, <c>WarehouseCode</c>
/// came off the query string and was checked against nobody, so any authenticated staff account could
/// read any shop's takings. Making it the first parameter means a new call site cannot compile without
/// supplying one.
/// </remarks>
public sealed record GetDesktopSalesQuery(
    Guid CallerUserId,
    string? WarehouseCode = null,
    string? CardCode = null,
    string? ConsolidationStatus = null,
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    int Page = 1,
    int PageSize = 50,
    string? SourceSystem = null
) : IRequest<ErrorOr<DesktopSalesListResult>>;

public sealed record DesktopSalesListResult(
    List<DesktopSaleListItemDto> Sales,
    int TotalCount,
    int Page,
    int PageSize,
    bool HasMore
);

public sealed record DesktopSaleListItemDto(
    int Id,
    string ExternalReferenceId,
    string? SourceSystem,
    string CardCode,
    string? CardName,
    DateTime DocDate,
    decimal TotalAmount,
    decimal VatAmount,
    string Currency,
    string FiscalizationStatus,
    string? FiscalReceiptNumber,

    // The receipt the device issued, so a reprint carries the fiscal block the original did. It is
    // held here and nowhere else a caller can reach: the receipt was signed under the sale's own
    // external reference rather than a SAP document number, so the invoice this sale eventually
    // becomes is marked fiscalised without the QR ever being restated on it.
    string? FiscalQRCode,
    string? FiscalVerificationCode,
    string? FiscalDeviceNumber,
    string? FiscalDayNo,

    string ConsolidationStatus,
    int? ConsolidationId,
    string WarehouseCode,
    string? PaymentMethod,
    string? PaymentReference,
    decimal AmountPaid,
    string? CreatedBy,
    DateTime CreatedAt,

    // --- Where this sale got to on its way to SAP ---
    //
    // The console offers a manual post, so it has to be able to say what there is to post and what
    // happened last time. Without these the only thing a page could show was "Awaiting close", which
    // is equally true of a sale uploaded a minute ago and one SAP has refused six times.

    // The SAP A/R invoice this sale posted as, for the one-to-one routes, and how many times it has
    // been offered to SAP so far.
    int? SapDocEntry,
    int? SapDocNum,
    DateTime? PostedAt,
    int PostingAttempts,
    string? LastPostingError,

    // How the settlement went: null (not attempted), "Posted", "PostedUnconfirmed", "Failed" or
    // "Unmapped". Reported beside the invoice because the two fail independently — an invoice can
    // post and its payment not, leaving a real open A/R document that must not be re-invoiced.
    // The payment's own document number goes with it, because chasing an unsettled invoice in SAP
    // starts with knowing whether there is a payment to look at.
    string? PaymentStatus,
    int? PaymentSapDocNum,

    // Why this sale may not be posted on request, or null when it may be. From
    // DesktopSalePostEligibility, which is the same rule the posting command refuses on — so a row
    // the console offers a button for is a row the command accepts.
    string? PostRefusal,

    List<DesktopSaleLineItemDto> Lines
)
{
    /// <summary>
    /// Whether the console should offer this sale a "Post to SAP" button.
    /// </summary>
    /// <remarks>
    /// Derived rather than carried, so it cannot contradict the reason beside it.
    /// </remarks>
    public bool CanPostToSap => PostRefusal is null;
}

public sealed record DesktopSaleLineItemDto(
    int LineNum,
    string ItemCode,
    string? ItemDescription,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineTotal,
    string WarehouseCode,
    string? TaxCode,
    decimal DiscountPercent
);
