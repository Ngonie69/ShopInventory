using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Crates.Commands.DeleteCrateOpeningBalance;

public sealed record DeleteCrateOpeningBalanceCommand(int CrateTransactionId, Guid? UserId) : IRequest<ErrorOr<bool>>;