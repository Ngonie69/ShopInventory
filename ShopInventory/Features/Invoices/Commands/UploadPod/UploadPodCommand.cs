using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Invoices.Commands.UploadPod;

// IsAdditionalPage: the caller means this to be a further page on a POD it has already uploaded,
// not a retry of one. Suppresses the double-submit window, which would otherwise read a second
// photo taken moments after the first as the same one arriving twice.
public sealed record UploadPodCommand(
    int DocEntry,
    Stream FileStream,
    string FileName,
    string ContentType,
    string? Description,
    string? UploadedByUsername,
    string? ExternalReference,
    Guid? UserId,
    bool IsAdditionalPage = false
) : IRequest<ErrorOr<DocumentAttachmentDto>>;
