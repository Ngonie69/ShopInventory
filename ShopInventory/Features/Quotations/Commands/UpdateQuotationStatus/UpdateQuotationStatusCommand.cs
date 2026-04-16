using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.Quotations.Commands.UpdateQuotationStatus;

public sealed record UpdateQuotationStatusCommand(
    int Id,
    QuotationStatus Status,
    Guid UserId,
    string? Comments
) : IRequest<ErrorOr<QuotationDto>>;
