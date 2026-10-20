using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Invoices.Commands.UploadPod;

// IsAdditionalPage: the caller means this to be a further page on a POD it has already uploaded,
// not a retry of one. Suppresses the double-submit window, which would otherwise read a second
// photo taken moments after the first as the same one arriving twice.
// FleetNumber: the truck the driver delivered with, as they typed it — see PodFleetNumber.
public sealed record UploadPodCommand(
    int DocEntry,
    Stream FileStream,
    string FileName,
    string ContentType,
    string? Description,
    string? UploadedByUsername,
    string? ExternalReference,
    Guid? UserId,
    bool IsAdditionalPage = false,
    string? FleetNumber = null
) : IRequest<ErrorOr<DocumentAttachmentDto>>;
