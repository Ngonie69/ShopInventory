using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Quotations.Commands.ApproveQuotation;

public sealed record ApproveQuotationCommand(
    int Id,
    Guid UserId
) : IRequest<ErrorOr<QuotationDto>>;
