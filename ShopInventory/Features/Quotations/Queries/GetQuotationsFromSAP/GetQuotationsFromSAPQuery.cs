using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Quotations.Queries.GetQuotationsFromSAP;

public sealed record GetQuotationsFromSAPQuery(
    int Page,
    int PageSize,
    string? CardCode,
    DateTime? FromDate,
    DateTime? ToDate
) : IRequest<ErrorOr<QuotationListResponseDto>>;
