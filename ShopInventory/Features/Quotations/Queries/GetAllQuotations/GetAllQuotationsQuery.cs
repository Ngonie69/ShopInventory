using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.Quotations.Queries.GetAllQuotations;

public sealed record GetAllQuotationsQuery(
    int Page,
    int PageSize,
    QuotationStatus? Status,
    string? CardCode,
    DateTime? FromDate,
    DateTime? ToDate
) : IRequest<ErrorOr<QuotationListResponseDto>>;
