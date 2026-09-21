using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.SapUserAccounts.Commands.UnlockSapUserAccount;

/// <summary>
/// Clear SAP's lock on one account. <paramref name="UserCode" /> is carried for the log line only —
/// SAP is addressed by the key.
/// </summary>
public sealed record UnlockSapUserAccountCommand(
    int InternalKey,
    string UserCode
) : IRequest<ErrorOr<SapUserAccountModel>>;
