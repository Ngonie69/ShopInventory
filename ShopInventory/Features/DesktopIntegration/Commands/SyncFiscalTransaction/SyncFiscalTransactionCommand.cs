using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;

/// <param name="Request">The transaction as the caller reports it.</param>
/// <param name="UserId">Who recorded it, when a person did.</param>
/// <param name="Username">The same person's name.</param>
/// <param name="RepostedAfterSapUpdate">
/// Recorded on the row as <c>RepostedAfterSapUpdate</c>. On the command rather than the request because the
/// request is the desktop apps' wire contract, and only this API reads the SAP remarks that decide it.
/// </param>
public sealed record SyncFiscalTransactionCommand(
    SyncFiscalTransactionRequest Request,
    string? UserId,
    string? Username,
    bool RepostedAfterSapUpdate = false) : IRequest<ErrorOr<FiscalTransactionLogItemDto>>;
