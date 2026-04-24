using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.PurchaseQuotations.Queries.GetPurchaseQuotations;

public sealed record GetPurchaseQuotationsQuery(
    int Page = 1,
    int PageSize = 20,
    string? CardCode = null,
    DateTime? FromDate = null,
    DateTime? ToDate = null
) : IRequest<ErrorOr<PurchaseQuotationListResponseDto>>;