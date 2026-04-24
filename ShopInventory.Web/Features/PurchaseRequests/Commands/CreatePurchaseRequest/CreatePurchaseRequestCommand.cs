using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.PurchaseRequests.Commands.CreatePurchaseRequest;

public sealed record CreatePurchaseRequestCommand(CreatePurchaseRequestRequest Request) : IRequest<ErrorOr<PurchaseRequestDto>>;