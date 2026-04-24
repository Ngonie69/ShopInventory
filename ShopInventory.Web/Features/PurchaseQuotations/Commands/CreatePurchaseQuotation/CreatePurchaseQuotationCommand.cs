using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.PurchaseQuotations.Commands.CreatePurchaseQuotation;

public sealed record CreatePurchaseQuotationCommand(CreatePurchaseQuotationRequest Request) : IRequest<ErrorOr<PurchaseQuotationDto>>;