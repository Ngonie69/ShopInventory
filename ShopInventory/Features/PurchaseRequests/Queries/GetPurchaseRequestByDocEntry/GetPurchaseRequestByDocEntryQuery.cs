using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.PurchaseRequests.Queries.GetPurchaseRequestByDocEntry;

public sealed record GetPurchaseRequestByDocEntryQuery(int DocEntry) : IRequest<ErrorOr<PurchaseRequestDto>>;