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

    // Where the sale ended up once the consolidation run had it. `ConsolidationStatus` says the run
    // took it; these two say the back office actually holds it, which is a different question and the
    // last one a caller can ask about a desktop sale without going to SAP.
    int? SapDocNum,
    DateTime? PostedAt,

    string WarehouseCode,
    string? PaymentMethod,
    string? PaymentReference,
    decimal AmountPaid,
    string? CreatedBy,
    DateTime CreatedAt,
    List<DesktopSaleLineItemDto> Lines
);

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
