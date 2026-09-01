using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.VanSalesCustomerAccounts.Commands.OnboardVanSalesCustomerAccount;

/// <summary>
/// Sends the operator's form to the API and turns a refusal into something to show them.
/// </summary>
/// <remarks>
/// The rules this can fall foul of — the number already belongs to another shop, the shop is
/// inactive, the number is not a number — all live on the API and are enforced there. This does not
/// re-check them: a second copy would drift, and the one that matters is the one the API applies.
/// It only carries the API's sentence back.
/// <para>
/// Nor does it audit. The API writes <c>CreateVanSalesCustomerAccount</c> against the account it
/// just made, with the authenticated operator on it; a second entry written here would be the same
/// event under a different id, and the two would disagree the first time either side changed.
/// </para>
/// </remarks>
public sealed class OnboardVanSalesCustomerAccountHandler(
    IVanSalesCustomerAccountService accountService,
    ILogger<OnboardVanSalesCustomerAccountHandler> logger
) : IRequestHandler<OnboardVanSalesCustomerAccountCommand, ErrorOr<VanSalesCustomerAccountModel>>
{
    public async Task<ErrorOr<VanSalesCustomerAccountModel>> Handle(
        OnboardVanSalesCustomerAccountCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var account = await accountService.OnboardAsync(
                new OnboardVanSalesCustomerAccountModel
                {
                    RouteCustomerId = request.RouteCustomerId,
                    PhoneNumber = request.PhoneNumber.Trim(),
                    DisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
                        ? null
                        : request.DisplayName.Trim()
                },
                cancellationToken);

            logger.LogInformation(
                "Set up a van sales customer sign-in for route customer {Code}.",
                account.RouteCustomerCode);

            return account;
        }
        catch (InvalidOperationException ex)
        {
            // The API's own explanation, already extracted by the service. Shown verbatim because
            // it names the shop or the number the operator has to go and check.
            return Errors.VanSalesCustomerAccount.OnboardFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error setting up a van sales customer sign-in");
            return Errors.VanSalesCustomerAccount.OnboardFailed("The sign-in could not be set up.");
        }
    }
}
