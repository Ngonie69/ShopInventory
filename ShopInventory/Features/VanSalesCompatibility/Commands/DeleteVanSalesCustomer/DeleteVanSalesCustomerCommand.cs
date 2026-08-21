using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.DeleteVanSalesCustomer;

/// <summary>
/// Removes a customer from the route the signed-in handset sells on, by the code the handset knows
/// it by.
/// </summary>
/// <remarks>
/// By code rather than by id because the handset has never been told the id. The van-sales customer
/// payload carries a compatibility id derived from the code, so there is no route customer id on a
/// handset to send back.
/// </remarks>
public sealed record DeleteVanSalesCustomerCommand(
    Guid UserId,
    string Code) : IRequest<ErrorOr<Deleted>>;
