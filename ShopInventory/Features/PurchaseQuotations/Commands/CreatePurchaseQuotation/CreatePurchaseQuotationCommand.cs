using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.PurchaseQuotations.Commands.CreatePurchaseQuotation;

public sealed record CreatePurchaseQuotationCommand(CreatePurchaseQuotationRequest Request) : IRequest<ErrorOr<PurchaseQuotationDto>>;