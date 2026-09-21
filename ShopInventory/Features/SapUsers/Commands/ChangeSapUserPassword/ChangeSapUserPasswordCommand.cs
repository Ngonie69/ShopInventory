using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.SapUsers.Commands.ChangeSapUserPassword;

/// <summary>
/// Sets a SAP user account's password. The password goes to SAP and nowhere else — it is not
/// audited, logged or answered back. Who is doing it is resolved by the handler; see
/// <see cref="UnlockSapUserAccount.UnlockSapUserAccountCommand" /> for why it is not a field here.
/// </summary>
public sealed record ChangeSapUserPasswordCommand(
    int InternalKey,
    string NewPassword
) : IRequest<ErrorOr<SapUserAccountDto>>;
