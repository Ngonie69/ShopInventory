using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Quotations.Commands.ApplyStandardVat;

public sealed record ApplyStandardVatCommand(int Id) : IRequest<ErrorOr<QuotationDto>>;