using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Commands.BackfillFiscalTransactions;

public sealed record BackfillFiscalTransactionsCommand(
    BackfillFiscalTransactionsRequest Request,
    string? UserId,
    string? Username) : IRequest<ErrorOr<BackfillFiscalTransactionsResult>>;