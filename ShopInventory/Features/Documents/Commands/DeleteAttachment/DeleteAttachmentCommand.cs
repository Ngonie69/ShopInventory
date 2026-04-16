using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Documents.Commands.DeleteAttachment;

public sealed record DeleteAttachmentCommand(int Id) : IRequest<ErrorOr<bool>>;
