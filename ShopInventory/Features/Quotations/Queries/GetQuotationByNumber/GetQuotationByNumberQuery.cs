using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Quotations.Queries.GetQuotationByNumber;

public sealed record GetQuotationByNumberQuery(
    string QuotationNumber
) : IRequest<ErrorOr<QuotationDto>>;
