using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseOrders.Commands.UploadPurchaseOrderDocument;

public sealed class UploadPurchaseOrderDocumentHandler(
    IDocumentService documentService,
    IAuditService auditService,
    ILogger<UploadPurchaseOrderDocumentHandler> logger
) : IRequestHandler<UploadPurchaseOrderDocumentCommand, ErrorOr<DocumentAttachmentDto>>
{
    private static readonly HashSet<string> AllowedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "application/pdf"
    };

    private const long MaxFileSizeBytes = 20 * 1024 * 1024; // 20 MB

    public async Task<ErrorOr<DocumentAttachmentDto>> Handle(
        UploadPurchaseOrderDocumentCommand command,
        CancellationToken cancellationToken)
    {
        if (command.FileBytes.Length == 0)
            return Errors.PurchaseOrder.UploadFailed("No file was uploaded.");

        if (command.FileBytes.Length > MaxFileSizeBytes)
            return Errors.PurchaseOrder.UploadFailed("File size exceeds the maximum allowed size of 20 MB.");

        if (!AllowedMimeTypes.Contains(command.ContentType))
            return Errors.PurchaseOrder.UploadFailed("File type not allowed. Accepted types: JPEG, PNG, WebP, PDF.");

        try
        {
            var request = new UploadAttachmentRequest
            {
                EntityType = "ExternalPurchaseOrder",
                EntityId = 0,
                ExternalReference = command.PoReferenceNumber,
                Description = command.Description ?? "Physical purchase order document",
                IncludeInEmail = false
            };

            using var stream = new MemoryStream(command.FileBytes);
            var attachment = await documentService.UploadAttachmentAsync(
                request, stream, command.FileName, command.ContentType, command.UserId, cancellationToken);

            try
            {
                await auditService.LogAsync(
                    AuditActions.UploadPurchaseOrderDocument,
                    "ExternalPurchaseOrder",
                    command.PoReferenceNumber,
                    $"Document '{command.FileName}' uploaded for external PO '{command.PoReferenceNumber}'",
                    true);
            }
            catch { /* audit failure should not block the operation */ }

            logger.LogInformation("Document uploaded for external PO '{PoReferenceNumber}' by user {UserId}",
                command.PoReferenceNumber, command.UserId);

            return attachment;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error uploading document for external PO '{PoReferenceNumber}'", command.PoReferenceNumber);
            return Errors.PurchaseOrder.UploadFailed(ex.Message);
        }
    }
}
