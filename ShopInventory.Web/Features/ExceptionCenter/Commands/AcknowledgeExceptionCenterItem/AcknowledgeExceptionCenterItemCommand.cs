using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.ExceptionCenter.Commands.AcknowledgeExceptionCenterItem;

public sealed record AcknowledgeExceptionCenterItemCommand(string Source, string ItemKey) : IRequest<ErrorOr<Success>>;