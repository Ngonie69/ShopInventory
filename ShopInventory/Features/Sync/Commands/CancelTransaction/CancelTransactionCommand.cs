using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Sync.Commands.CancelTransaction;

public sealed record CancelTransactionCommand(int Id) : IRequest<ErrorOr<Success>>;
