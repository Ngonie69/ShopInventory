using ErrorOr;
using MediatR;

namespace ShopInventory.Features.ExceptionCenter.Commands.AcknowledgeExceptionCenterItem;

public sealed record AcknowledgeExceptionCenterItemCommand(string Source, int ItemId) : IRequest<ErrorOr<Success>>;