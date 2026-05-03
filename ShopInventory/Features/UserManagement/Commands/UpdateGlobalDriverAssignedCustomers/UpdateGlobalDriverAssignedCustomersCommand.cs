using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.UserManagement.Commands.UpdateGlobalDriverAssignedCustomers;

public sealed record UpdateGlobalDriverAssignedCustomersCommand(
    UpdateGlobalDriverAssignedCustomersRequest Request
) : IRequest<ErrorOr<int>>;