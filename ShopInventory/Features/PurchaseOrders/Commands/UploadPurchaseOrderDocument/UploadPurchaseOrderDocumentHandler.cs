using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;
using System.IO;

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

    private static readonly Dictionary<string, string> MimeTypesByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
        [".pdf"] = "application/pdf"
    };

    private const long MaxFileSizeBytes = 20 * 1024 * 1024; // 20 MB

    public async Task<ErrorOr<DocumentAttachmentDto>> Handle(
        UploadPurchaseOrderDocumentCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.PoReferenceNumber))
            return Errors.PurchaseOrder.UploadValidationFailed("A purchase order reference is required.");

        if (command.FileBytes.Length == 0)
            return Errors.PurchaseOrder.UploadValidationFailed("No file was uploaded.");

        if (command.FileBytes.Length > MaxFileSizeBytes)
            return Errors.PurchaseOrder.UploadValidationFailed("File size exceeds the maximum allowed size of 20 MB.");

        var normalizedContentType = NormalizeContentType(command.FileName, command.ContentType);
        if (!AllowedMimeTypes.Contains(normalizedContentType))
            return Errors.PurchaseOrder.UploadValidationFailed("File type not allowed. Accepted types: JPEG, PNG, WebP, PDF.");

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
                request, stream, command.FileName, normalizedContentType, command.UserId, cancellationToken);

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

    private static string NormalizeContentType(string fileName, string contentType)
    {
        var normalizedContentType = contentType.Split(';', 2)[0].Trim();

        if (string.Equals(normalizedContentType, "image/jpg", StringComparison.OrdinalIgnoreCase))
            normalizedContentType = "image/jpeg";

        if (AllowedMimeTypes.Contains(normalizedContentType))
            return normalizedContentType;

        var extension = Path.GetExtension(fileName);
        if (!string.IsNullOrWhiteSpace(extension) && MimeTypesByExtension.TryGetValue(extension, out var inferredContentType))
            return inferredContentType;

        return normalizedContentType;
    }
}
