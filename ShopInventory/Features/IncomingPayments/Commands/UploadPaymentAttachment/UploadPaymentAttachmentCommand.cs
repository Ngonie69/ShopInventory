using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.IncomingPayments.Commands.UploadPaymentAttachment;

public sealed record UploadPaymentAttachmentCommand(
    int DocEntry,
    Stream FileStream,
    string FileName,
    string ContentType,
    string? Description,
    Guid UserId
) : IRequest<ErrorOr<DocumentAttachmentDto>>;
