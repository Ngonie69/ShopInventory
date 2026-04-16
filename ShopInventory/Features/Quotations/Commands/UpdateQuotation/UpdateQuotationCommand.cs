using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Quotations.Commands.UpdateQuotation;

public sealed record UpdateQuotationCommand(
    int Id,
    CreateQuotationRequest Request
) : IRequest<ErrorOr<QuotationDto>>;
