using ShopInventory.DTOs;
using ErrorOr;
using MediatR;

namespace ShopInventory.Features.CustomerPortal.Commands.BulkRegisterCustomers;

public sealed record BulkRegisterCustomersCommand(
    string DefaultPassword,
    List<CustomerBasicInfo> Customers
) : IRequest<ErrorOr<BulkRegistrationResponse>>;
