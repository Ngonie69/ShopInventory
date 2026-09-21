using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.SapUserAccounts.Queries.GetSapUserAccounts;

/// <summary>
/// Loads the SAP user accounts for the screen.
/// </summary>
/// <remarks>
/// The search and the locked filter are pushed to the API rather than applied to a list held here:
/// whether an account is locked right now is the whole question this screen answers, and a list held
/// from a minute ago answers it wrongly at exactly the moment somebody is waiting at the desk.
/// </remarks>
public sealed class GetSapUserAccountsHandler(
    ISapUserAccountService accountService,
    ILogger<GetSapUserAccountsHandler> logger
) : IRequestHandler<GetSapUserAccountsQuery, ErrorOr<SapUserAccountListModel>>
{
    public async Task<ErrorOr<SapUserAccountListModel>> Handle(
        GetSapUserAccountsQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await accountService.GetAccountsAsync(request.Search, request.LockedOnly, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading SAP user accounts");
            return Errors.SapUserAccount.LoadFailed("Failed to load SAP user accounts.");
        }
    }
}
