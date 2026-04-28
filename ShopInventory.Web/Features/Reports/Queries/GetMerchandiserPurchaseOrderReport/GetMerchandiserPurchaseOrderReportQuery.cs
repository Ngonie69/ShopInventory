using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Reports.Queries.GetMerchandiserPurchaseOrderReport;

public sealed record GetMerchandiserPurchaseOrderReportQuery(
    DateTime? FromDate,
    DateTime? ToDate,
    Guid? MerchandiserUserId,
    bool? HasAttachments,
    string? Search
) : IRequest<ErrorOr<GetMerchandiserPurchaseOrderReportResult>>;