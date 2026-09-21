using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.SapUserAccounts.Commands.ChangeSapUserPassword;

/// <summary>
/// Set a new password on one SAP account. The password travels to the API and no further: it is not
/// logged, not stored and not put on the audit row this raises.
/// </summary>
public sealed record ChangeSapUserPasswordCommand(
    int InternalKey,
    string UserCode,
    string NewPassword
) : IRequest<ErrorOr<SapUserAccountModel>>;
