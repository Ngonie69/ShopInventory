using ErrorOr;
using MediatR;
using ShopInventory.Features.DesktopIntegration.Queries.GetFiscalTransactions;

namespace ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;

public sealed record SyncFiscalTransactionCommand(
    SyncFiscalTransactionRequest Request,
    string? UserId,
    string? Username) : IRequest<ErrorOr<FiscalTransactionLogItemDto>>;