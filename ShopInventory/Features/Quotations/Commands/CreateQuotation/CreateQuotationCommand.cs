using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Quotations.Commands.CreateQuotation;

public sealed record CreateQuotationCommand(
    CreateQuotationRequest Request,
    Guid UserId
) : IRequest<ErrorOr<QuotationDto>>;
