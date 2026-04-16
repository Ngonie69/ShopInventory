using ErrorOr;
using MediatR;

namespace ShopInventory.Features.PurchaseOrders.Commands.DeletePurchaseOrder;

public sealed record DeletePurchaseOrderCommand(int Id) : IRequest<ErrorOr<Deleted>>;
