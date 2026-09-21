using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.SapUserAccounts.Commands.UnlockSapUserAccount;

/// <summary>Unlocks through the API and reports what it said if SAP refused.</summary>
public sealed class UnlockSapUserAccountHandler(
    ISapUserAccountService accountService,
    ILogger<UnlockSapUserAccountHandler> logger
) : IRequestHandler<UnlockSapUserAccountCommand, ErrorOr<SapUserAccountModel>>
{
    public async Task<ErrorOr<SapUserAccountModel>> Handle(
        UnlockSapUserAccountCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var account = await accountService.UnlockAsync(request.InternalKey, cancellationToken);

            logger.LogInformation(
                "Unlocked SAP user account {InternalKey} ({UserCode})", request.InternalKey, request.UserCode);

            return account;
        }
        catch (InvalidOperationException ex)
        {
            // The API's sentence, which is usually SAP's own. It names what to do next.
            return Errors.SapUserAccount.UnlockFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error unlocking SAP user account {InternalKey}", request.InternalKey);
            return Errors.SapUserAccount.UnlockFailed("The account could not be unlocked.");
        }
    }
}
