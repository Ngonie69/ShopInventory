using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesCustomerAuth.Commands.DeactivateVanSalesCustomerAccount;

/// <summary>Stop a phone from signing in and end every session it currently holds.</summary>
public sealed record DeactivateVanSalesCustomerAccountCommand(
    int AccountId
) : IRequest<ErrorOr<VanSalesCustomerAccountResult>>;
