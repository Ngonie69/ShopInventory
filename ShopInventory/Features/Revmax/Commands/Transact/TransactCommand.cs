using ErrorOr;
using MediatR;
using ShopInventory.Models.Revmax;

namespace ShopInventory.Features.Revmax.Commands.Transact;

public sealed record TransactCommand(TransactMRequest Request) : IRequest<ErrorOr<TransactMResponse>>;
