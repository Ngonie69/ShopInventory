using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.SapUsers.Commands.UnlockSapUserAccount;

/// <summary>
/// Clears SAP's lock on one user account. Who is doing it is not carried here — the handler reads
/// the acting account rather than trusting the request to name it, because the Web sends its
/// integration key alongside the user's token and the key would otherwise sign the audit row.
/// </summary>
public sealed record UnlockSapUserAccountCommand(
    int InternalKey
) : IRequest<ErrorOr<SapUserAccountDto>>;
