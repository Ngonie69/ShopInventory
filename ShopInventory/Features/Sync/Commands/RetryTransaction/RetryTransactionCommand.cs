using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Sync.Commands.RetryTransaction;

public sealed record RetryTransactionCommand(int Id) : IRequest<ErrorOr<Success>>;
