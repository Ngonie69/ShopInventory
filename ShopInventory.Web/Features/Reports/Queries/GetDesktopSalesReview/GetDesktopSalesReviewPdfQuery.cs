using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesReview;

/// <summary>The same review as a PDF, for download.</summary>
public sealed record GetDesktopSalesReviewPdfQuery(DateTime FromDate, DateTime ToDate, string? WarehouseCode)
    : IRequest<ErrorOr<byte[]>>;
