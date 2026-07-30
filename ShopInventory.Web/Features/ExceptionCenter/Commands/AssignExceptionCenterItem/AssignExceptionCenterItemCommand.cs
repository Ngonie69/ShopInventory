using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.ExceptionCenter.Commands.AssignExceptionCenterItem;

public sealed record AssignExceptionCenterItemCommand(string Source, string ItemKey) : IRequest<ErrorOr<Success>>;