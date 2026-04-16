using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.PurchaseOrders.Queries.GetPurchaseOrderFromSAPByDocEntry;

public sealed record GetPurchaseOrderFromSAPByDocEntryQuery(int DocEntry) : IRequest<ErrorOr<PurchaseOrderDto>>;
