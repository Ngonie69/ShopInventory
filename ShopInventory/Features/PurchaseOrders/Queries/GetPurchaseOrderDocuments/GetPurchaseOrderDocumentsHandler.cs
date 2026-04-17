using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseOrders.Queries.GetPurchaseOrderDocuments;

public sealed class GetPurchaseOrderDocumentsHandler(
    ApplicationDbContext db,
    ILogger<GetPurchaseOrderDocumentsHandler> logger
) : IRequestHandler<GetPurchaseOrderDocumentsQuery, ErrorOr<DocumentAttachmentListResponseDto>>
{
    public async Task<ErrorOr<DocumentAttachmentListResponseDto>> Handle(
        GetPurchaseOrderDocumentsQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var dbQuery = db.Set<DocumentAttachmentEntity>()
                .Include(a => a.UploadedByUser)
                .Where(a => a.EntityType == "ExternalPurchaseOrder")
                .AsNoTracking();

            if (!string.IsNullOrWhiteSpace(query.PoReferenceNumber))
                dbQuery = dbQuery.Where(a => a.ExternalReference == query.PoReferenceNumber);

            var attachments = await dbQuery
                .OrderByDescending(a => a.UploadedAt)
                .ToListAsync(cancellationToken);

            return new DocumentAttachmentListResponseDto
            {
                Attachments = attachments.Select(a => new DocumentAttachmentDto
                {
                    Id = a.Id,
                    EntityType = a.EntityType,
                    EntityId = a.EntityId,
                    ExternalReference = a.ExternalReference,
                    FileName = a.FileName,
                    MimeType = a.MimeType,
                    FileSizeBytes = a.FileSizeBytes,
                    FileSizeFormatted = FormatFileSize(a.FileSizeBytes),
                    Description = a.Description,
                    IncludeInEmail = a.IncludeInEmail,
                    UploadedAt = a.UploadedAt,
                    UploadedByUserName = a.UploadedByUser?.Username,
                    DownloadUrl = $"/api/document/attachment/{a.Id}/download"
                }).ToList(),
                TotalCount = attachments.Count
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving external PO documents");
            return Error.Failure("PurchaseOrder.QueryFailed", $"Failed to retrieve documents: {ex.Message}");
        }
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB"];
        var order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }
}
