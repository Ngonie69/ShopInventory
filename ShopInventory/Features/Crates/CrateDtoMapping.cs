using ShopInventory.Common.Crates;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.Crates;

internal static class CrateDtoMapping
{
    public static CrateTransactionDto MapTransaction(
        CrateTransactionEntity transaction,
        CratePodSubmissionEntity? driverSubmission,
        CratePodSubmissionEntity? merchandiserSubmission,
        int supportingDocumentCount,
        int driverAttachmentCount,
        int merchandiserAttachmentCount)
    {
        return new CrateTransactionDto
        {
            Id = transaction.Id,
            TransactionType = transaction.TransactionType,
            InvoiceDocEntry = transaction.InvoiceDocEntry,
            InvoiceDocNum = transaction.InvoiceDocNum,
            ShopCardCode = transaction.ShopCardCode,
            ShopName = transaction.ShopName,
            ExpectedQuantity = transaction.ExpectedQuantity,
            DriverQuantity = driverSubmission?.Quantity,
            MerchandiserQuantity = merchandiserSubmission?.Quantity,
            VarianceQuantity = merchandiserSubmission is null
                ? null
                : merchandiserSubmission.Quantity - transaction.ExpectedQuantity,
            HasDriverPod = driverSubmission is not null,
            HasMerchandiserPod = merchandiserSubmission is not null,
            HasDriverPodDocument = driverAttachmentCount > 0,
            HasMerchandiserPodDocument = merchandiserAttachmentCount > 0,
            HasGrv = transaction.Grv is not null,
            GrvId = transaction.Grv?.Id,
            GrvNumber = transaction.Grv?.GrvNumber,
            SupportingDocumentCount = supportingDocumentCount,
            EffectiveDate = transaction.EffectiveDate,
            CreatedAt = transaction.CreatedAt,
            CreatedByUserName = transaction.CreatedByUser?.Username,
            Status = DetermineStatus(transaction, driverSubmission, merchandiserSubmission),
            Notes = transaction.Notes
        };
    }

    public static CratePodSubmissionDto MapPodSubmission(
        CratePodSubmissionEntity submission,
        List<DocumentAttachmentDto> attachments)
    {
        return new CratePodSubmissionDto
        {
            Id = submission.Id,
            CrateTransactionId = submission.CrateTransactionId,
            InvoiceDocNum = submission.CrateTransaction.InvoiceDocNum,
            ShopCardCode = submission.CrateTransaction.ShopCardCode,
            ShopName = submission.CrateTransaction.ShopName,
            ExpectedQuantity = submission.CrateTransaction.ExpectedQuantity,
            SubmissionRole = submission.SubmissionRole,
            Quantity = submission.Quantity,
            SubmittedAt = submission.SubmittedAt,
            SubmittedByUserName = submission.SubmittedByUser?.Username,
            Notes = submission.Notes,
            Attachments = attachments
        };
    }

    public static CrateGrvDto MapGrv(
        CrateGrvEntity grv,
        List<DocumentAttachmentDto> attachments)
    {
        return new CrateGrvDto
        {
            Id = grv.Id,
            CrateTransactionId = grv.CrateTransactionId,
            GrvNumber = grv.GrvNumber,
            InvoiceDocNum = grv.CrateTransaction.InvoiceDocNum,
            ShopCardCode = grv.CrateTransaction.ShopCardCode,
            ShopName = grv.CrateTransaction.ShopName,
            ExpectedQuantity = grv.ExpectedQuantity,
            ActualQuantity = grv.ActualQuantity,
            VarianceQuantity = grv.VarianceQuantity,
            Direction = grv.Direction,
            Reason = grv.Reason,
            Status = grv.Status,
            CreatedAt = grv.CreatedAt,
            CreatedByUserName = grv.CreatedByUser?.Username,
            Attachments = attachments
        };
    }

    public static DocumentAttachmentDto MapAttachment(DocumentAttachmentEntity attachment)
    {
        return new DocumentAttachmentDto
        {
            Id = attachment.Id,
            EntityType = attachment.EntityType,
            EntityId = attachment.EntityId,
            ExternalReference = attachment.ExternalReference,
            FileName = attachment.FileName,
            MimeType = attachment.MimeType,
            FileSizeBytes = attachment.FileSizeBytes,
            FileSizeFormatted = FormatFileSize(attachment.FileSizeBytes),
            Description = attachment.Description,
            IncludeInEmail = attachment.IncludeInEmail,
            UploadedAt = attachment.UploadedAt,
            UploadedByUserName = attachment.UploadedByUser?.Username,
            DownloadUrl = $"/api/document/attachments/{attachment.Id}/download"
        };
    }

    public static string DetermineStatus(
        CrateTransactionEntity transaction,
        CratePodSubmissionEntity? driverSubmission,
        CratePodSubmissionEntity? merchandiserSubmission)
    {
        if (string.Equals(transaction.TransactionType, CrateTrackingConstants.TransactionTypeOpeningBalance, StringComparison.OrdinalIgnoreCase))
        {
            return CrateTrackingConstants.StatusMatched;
        }

        if (driverSubmission is null)
        {
            return CrateTrackingConstants.StatusPendingDriverPod;
        }

        if (merchandiserSubmission is null)
        {
            return CrateTrackingConstants.StatusPendingMerchandiserPod;
        }

        if (transaction.Grv is not null)
        {
            return CrateTrackingConstants.StatusGrvRaised;
        }

        return merchandiserSubmission.Quantity == transaction.ExpectedQuantity
            ? CrateTrackingConstants.StatusMatched
            : CrateTrackingConstants.StatusVariancePendingGrv;
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB"];
        double length = bytes;
        var order = 0;

        while (length >= 1024 && order < sizes.Length - 1)
        {
            order++;
            length /= 1024;
        }

        return $"{length:0.##} {sizes[order]}";
    }
}