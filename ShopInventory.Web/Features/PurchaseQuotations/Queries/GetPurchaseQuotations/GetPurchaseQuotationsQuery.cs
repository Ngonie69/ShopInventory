using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.PurchaseQuotations.Queries.GetPurchaseQuotations;

public sealed record GetPurchaseQuotationsQuery(
    int Page = 1,
    int PageSize = 20,
    string? CardCode = null,
    DateTime? FromDate = null,
    DateTime? ToDate = null
) : IRequest<ErrorOr<PurchaseQuotationListResponse>>;