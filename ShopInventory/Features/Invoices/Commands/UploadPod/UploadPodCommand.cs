using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Invoices.Commands.UploadPod;

public sealed record UploadPodCommand(
    int DocEntry,
    Stream FileStream,
    string FileName,
    string ContentType,
    string? Description,
    string? UploadedByUsername,
    Guid? UserId
) : IRequest<ErrorOr<DocumentAttachmentDto>>;
