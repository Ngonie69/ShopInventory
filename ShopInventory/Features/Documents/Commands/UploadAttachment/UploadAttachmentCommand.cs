using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Documents.Commands.UploadAttachment;

public sealed record UploadAttachmentCommand(
    string EntityType,
    int EntityId,
    string? Description,
    bool IncludeInEmail,
    byte[] FileBytes,
    string FileName,
    string ContentType,
    Guid UserId
) : IRequest<ErrorOr<DocumentAttachmentDto>>;
