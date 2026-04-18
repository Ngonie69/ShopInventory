using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Timesheets.Queries.GetAssignedCustomers;

public sealed record GetAssignedCustomersQuery(
    Guid UserId
) : IRequest<ErrorOr<List<AssignedCustomerDto>>>;

public sealed record AssignedCustomerDto(
    string CustomerCode,
    string CustomerName,
    bool HasActiveCheckIn
);
