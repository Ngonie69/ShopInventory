using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.UserManagement.Commands.UpdateMerchandiserAssignedCustomers;

public sealed record UpdateMerchandiserAssignedCustomersCommand(
    Guid Id,
    UpdateMerchandiserAssignedCustomersRequest Request
) : IRequest<ErrorOr<Success>>;