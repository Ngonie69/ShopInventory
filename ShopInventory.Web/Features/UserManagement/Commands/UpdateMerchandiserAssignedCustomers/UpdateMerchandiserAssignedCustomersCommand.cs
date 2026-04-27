using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.UserManagement.Commands.UpdateMerchandiserAssignedCustomers;

public sealed record UpdateMerchandiserAssignedCustomersCommand(
    Guid UserId,
    string Username,
    List<string> AssignedWarehouseCodes,
    List<string> AssignedCustomerCodes
) : IRequest<ErrorOr<string>>;