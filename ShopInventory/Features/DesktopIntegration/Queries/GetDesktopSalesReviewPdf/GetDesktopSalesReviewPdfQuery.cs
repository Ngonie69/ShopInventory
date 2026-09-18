using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewPdf;

/// <summary>
/// The desktop sales business review as a PDF — the same review the page shows, for printing and for
/// the scheduled email.
/// </summary>
/// <param name="CallerUserId">Whose read scope applies.</param>
/// <param name="FromDate">First business day, inclusive.</param>
/// <param name="ToDate">Last business day, inclusive.</param>
/// <param name="WarehouseCode">One shop or depot's warehouse, or every one the caller may read.</param>
/// <param name="Title">What the document calls the period — "Weekly review", say. Defaults to a plain review.</param>
public sealed record GetDesktopSalesReviewPdfQuery(
    Guid CallerUserId,
    DateTime? FromDate,
    DateTime? ToDate,
    string? WarehouseCode = null,
    string? Title = null
) : IRequest<ErrorOr<DesktopSalesReviewDocument>>;
