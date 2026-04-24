using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.PurchaseRequests.Commands.CreatePurchaseRequest;

public sealed record CreatePurchaseRequestCommand(CreatePurchaseRequestRequest Request) : IRequest<ErrorOr<PurchaseRequestDto>>;