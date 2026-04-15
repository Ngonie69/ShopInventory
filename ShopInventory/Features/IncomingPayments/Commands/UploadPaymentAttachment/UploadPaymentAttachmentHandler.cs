using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.IncomingPayments.Commands.UploadPaymentAttachment;

public sealed class UploadPaymentAttachmentHandler(
    IDocumentService documentService,
    ILogger<UploadPaymentAttachmentHandler> logger
) : IRequestHandler<UploadPaymentAttachmentCommand, ErrorOr<DocumentAttachmentDto>>
{
    public async Task<ErrorOr<DocumentAttachmentDto>> Handle(
        UploadPaymentAttachmentCommand command,
        CancellationToken cancellationToken)
    {
        var request = new UploadAttachmentRequest
        {
            EntityType = "IncomingPayment",
            EntityId = command.DocEntry,
            Description = command.Description ?? "Payment Attachment",
            IncludeInEmail = false
        };

        var attachment = await documentService.UploadAttachmentAsync(
            request, command.FileStream, command.FileName, command.ContentType, command.UserId, cancellationToken);

        logger.LogInformation("Attachment uploaded for incoming payment {DocEntry} by user {UserId}", command.DocEntry, command.UserId);

        return attachment;
    }
}
