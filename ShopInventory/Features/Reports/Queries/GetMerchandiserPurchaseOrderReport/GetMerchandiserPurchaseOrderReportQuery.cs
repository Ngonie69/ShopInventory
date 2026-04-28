using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Reports.Queries.GetMerchandiserPurchaseOrderReport;

public sealed record GetMerchandiserPurchaseOrderReportQuery(
    DateTime? FromDate,
    DateTime? ToDate,
    Guid? MerchandiserUserId,
    bool? HasAttachments,
    string? Search
) : IRequest<ErrorOr<MerchandiserPurchaseOrderReportDto>>;