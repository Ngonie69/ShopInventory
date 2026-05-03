using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.ExceptionCenter.Commands.AssignExceptionCenterItem;

public sealed record AssignExceptionCenterItemCommand(string Source, int ItemId) : IRequest<ErrorOr<Success>>;