using ErrorOr;
using MediatR;
using ShopInventory.Models.Revmax;

namespace ShopInventory.Features.Revmax.Commands.TransactExt;

public sealed record TransactExtCommand(TransactMExtRequest Request) : IRequest<ErrorOr<TransactMExtResponse>>;
