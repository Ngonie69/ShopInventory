using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.SapUserAccounts.Commands.ChangeSapUserPassword;

/// <summary>
/// Changes the password through the API and reports what it said if SAP refused.
/// </summary>
/// <remarks>
/// The log line names the account and not one thing about the password — not its length, not whether
/// it was accepted first try. A log that says how long a password was is a log that narrows it.
/// </remarks>
public sealed class ChangeSapUserPasswordHandler(
    ISapUserAccountService accountService,
    ILogger<ChangeSapUserPasswordHandler> logger
) : IRequestHandler<ChangeSapUserPasswordCommand, ErrorOr<SapUserAccountModel>>
{
    public async Task<ErrorOr<SapUserAccountModel>> Handle(
        ChangeSapUserPasswordCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var account = await accountService.ChangePasswordAsync(
                request.InternalKey, request.NewPassword, cancellationToken);

            logger.LogInformation(
                "Changed the password of SAP user account {InternalKey} ({UserCode})",
                request.InternalKey,
                request.UserCode);

            return account;
        }
        catch (InvalidOperationException ex)
        {
            // SAP names the policy rule that was broken; passing it through is the only useful answer.
            return Errors.SapUserAccount.ChangePasswordFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error changing the password of SAP user account {InternalKey}", request.InternalKey);
            return Errors.SapUserAccount.ChangePasswordFailed("The password could not be changed.");
        }
    }
}
