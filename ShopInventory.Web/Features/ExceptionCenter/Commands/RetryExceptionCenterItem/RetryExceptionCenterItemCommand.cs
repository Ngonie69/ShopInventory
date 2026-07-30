using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.ExceptionCenter.Commands.RetryExceptionCenterItem;

public sealed record RetryExceptionCenterItemCommand(string Source, string ItemKey) : IRequest<ErrorOr<Success>>;