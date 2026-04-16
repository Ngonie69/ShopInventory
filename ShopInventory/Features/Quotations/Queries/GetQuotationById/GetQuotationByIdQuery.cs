using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Quotations.Queries.GetQuotationById;

public sealed record GetQuotationByIdQuery(int Id) : IRequest<ErrorOr<QuotationDto>>;
