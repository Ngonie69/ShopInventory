using ErrorOr;
using MediatR;

namespace ShopInventory.Features.ExceptionCenter.Commands.RetryExceptionCenterItem;

public sealed record RetryExceptionCenterItemCommand(string Source, int ItemId) : IRequest<ErrorOr<Success>>;