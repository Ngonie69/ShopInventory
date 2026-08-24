using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesOrders.Commands.CancelVanSalesCustomerOrder;

/// <summary>A customer withdrawing an order they placed.</summary>
public sealed record CancelVanSalesCustomerOrderCommand(
    int AccountId,
    int OrderId,
    string? Reason
) : IRequest<ErrorOr<VanSalesOrderResult>>;
