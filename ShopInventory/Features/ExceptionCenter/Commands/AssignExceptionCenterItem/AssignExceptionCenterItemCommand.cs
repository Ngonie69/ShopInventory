using ErrorOr;
using MediatR;

namespace ShopInventory.Features.ExceptionCenter.Commands.AssignExceptionCenterItem;

public sealed record AssignExceptionCenterItemCommand(string Source, string ItemKey) : IRequest<ErrorOr<Success>>;