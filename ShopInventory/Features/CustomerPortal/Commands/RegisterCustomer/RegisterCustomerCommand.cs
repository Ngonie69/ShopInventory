using ShopInventory.DTOs;
using ErrorOr;
using MediatR;

namespace ShopInventory.Features.CustomerPortal.Commands.RegisterCustomer;

public sealed record RegisterCustomerCommand(
    string CardCode,
    string CardName,
    string Email,
    string Password
) : IRequest<ErrorOr<CustomerRegistrationResponse>>;
