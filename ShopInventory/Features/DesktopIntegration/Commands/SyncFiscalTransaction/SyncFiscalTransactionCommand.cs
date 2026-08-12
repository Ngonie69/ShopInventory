using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;

public sealed record SyncFiscalTransactionCommand(
    SyncFiscalTransactionRequest Request,
    string? UserId,
    string? Username) : IRequest<ErrorOr<FiscalTransactionLogItemDto>>;