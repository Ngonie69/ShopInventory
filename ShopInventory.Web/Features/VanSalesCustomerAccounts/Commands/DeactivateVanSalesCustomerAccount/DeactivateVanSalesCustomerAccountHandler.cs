using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.VanSalesCustomerAccounts.Commands.DeactivateVanSalesCustomerAccount;

/// <summary>Withdraws a sign-in through the API and reports what it said if it refused.</summary>
public sealed class DeactivateVanSalesCustomerAccountHandler(
    IVanSalesCustomerAccountService accountService,
    ILogger<DeactivateVanSalesCustomerAccountHandler> logger
) : IRequestHandler<DeactivateVanSalesCustomerAccountCommand, ErrorOr<VanSalesCustomerAccountModel>>
{
    public async Task<ErrorOr<VanSalesCustomerAccountModel>> Handle(
        DeactivateVanSalesCustomerAccountCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var account = await accountService.DeactivateAsync(request.AccountId, cancellationToken);

            logger.LogInformation(
                "Withdrew van sales customer sign-in {AccountId} for route customer {Code}.",
                account.Id,
                account.RouteCustomerCode);

            return account;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.VanSalesCustomerAccount.DeactivateFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error withdrawing van sales customer sign-in {AccountId}", request.AccountId);
            return Errors.VanSalesCustomerAccount.DeactivateFailed("The sign-in could not be withdrawn.");
        }
    }
}
