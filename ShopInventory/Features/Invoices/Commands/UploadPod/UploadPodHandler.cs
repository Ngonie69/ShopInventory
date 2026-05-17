using ErrorOr;
using MediatR;
using ShopInventory.Common.Pods;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.Notifications;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Invoices.Commands.UploadPod;

public sealed class UploadPodHandler(
    ISAPServiceLayerClient sapClient,
    IDocumentService documentService,
    IAuthService authService,
    IAuditService auditService,
    INotificationService notificationService,
    ILogger<UploadPodHandler> logger
) : IRequestHandler<UploadPodCommand, ErrorOr<DocumentAttachmentDto>>
{
    public async Task<ErrorOr<DocumentAttachmentDto>> Handle(
        UploadPodCommand command,
        CancellationToken cancellationToken)
    {
        // Validate that this invoice's BP is not excluded from PODs
        Invoice? invoiceInfo = null;
        try
        {
            invoiceInfo = await sapClient.GetInvoiceByDocEntryAsync(command.DocEntry, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not fetch invoice info for DocEntry {DocEntry} during POD upload", command.DocEntry);
        }

        if (invoiceInfo != null && PodExclusions.IsExcludedCardCode(invoiceInfo.CardCode))
            return Errors.Invoice.PodExcluded(invoiceInfo.CardName ?? "", invoiceInfo.CardCode ?? "");

        var request = new UploadAttachmentRequest
        {
            EntityType = "Invoice",
            EntityId = command.DocEntry,
            ExternalReference = string.IsNullOrWhiteSpace(command.ExternalReference)
                ? null
                : command.ExternalReference.Trim(),
            Description = string.IsNullOrWhiteSpace(command.Description)
                ? "POD - Proof of Delivery"
                : $"POD - {command.Description}",
            IncludeInEmail = false
        };

        // Cache invoice info from SAP
        if (invoiceInfo != null)
        {
            try
            {
                await documentService.EnsureInvoiceCachedAsync(
                    command.DocEntry, invoiceInfo.DocNum, invoiceInfo.CardCode ?? "", invoiceInfo.CardName, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not cache invoice info for DocEntry {DocEntry}", command.DocEntry);
            }
        }

        // Resolve uploader context for attachment ownership and targeted notifications.
        User? uploader = null;
        var userId = command.UserId;
        if (!string.IsNullOrWhiteSpace(command.UploadedByUsername))
        {
            uploader = await authService.GetUserByUsernameAsync(command.UploadedByUsername);
            userId ??= uploader?.Id;
        }

        if (!string.IsNullOrWhiteSpace(request.ExternalReference))
        {
            var existingAttachment = await documentService.GetAttachmentByExternalReferenceAsync(
                request.EntityType,
                request.EntityId,
                request.ExternalReference,
                cancellationToken);

            if (existingAttachment is not null)
            {
                logger.LogInformation(
                    "Skipping duplicate POD upload for invoice {DocEntry} with external reference {ExternalReference}",
                    command.DocEntry,
                    request.ExternalReference);
                return existingAttachment;
            }
        }

        // Prefix filename with POD_
        var fileName = command.FileName.StartsWith("POD", StringComparison.OrdinalIgnoreCase)
            ? command.FileName
            : $"POD_{command.FileName}";

        var attachment = await documentService.UploadAttachmentAsync(
            request, command.FileStream, fileName, command.ContentType, userId, cancellationToken);

        logger.LogInformation("POD uploaded for invoice {DocEntry} by user {UserId}", command.DocEntry, userId);

        try
        {
            await auditService.LogAsync(
                AuditActions.UploadPod,
                "Invoice",
                attachment.Id.ToString(),
                $"POD '{attachment.FileName}' uploaded for invoice {command.DocEntry}",
                true);
        }
        catch
        {
        }

        try
        {
            var invoiceLabel = invoiceInfo?.DocNum is int docNum
                ? $"invoice {docNum}"
                : $"invoice doc entry {command.DocEntry}";
            var customerDisplay = BuildBusinessPartnerDisplay(invoiceInfo?.CardCode, invoiceInfo?.CardName);
            var notificationData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["attachmentId"] = attachment.Id.ToString(),
                ["fileName"] = attachment.FileName,
                ["invoiceDocEntry"] = command.DocEntry.ToString(),
                ["invoiceDocNum"] = invoiceInfo?.DocNum.ToString() ?? string.Empty,
                ["cardCode"] = invoiceInfo?.CardCode ?? string.Empty,
                ["cardName"] = invoiceInfo?.CardName ?? string.Empty
            };
            var targetUsername = !string.IsNullOrWhiteSpace(uploader?.Username)
                ? uploader.Username
                : command.UploadedByUsername;
            CreateNotificationRequest notificationRequest;

            if (uploader is not null &&
                string.Equals(uploader.Role, "Driver", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(targetUsername))
            {
                notificationRequest = new CreateNotificationRequest
                {
                    Title = $"POD Uploaded: {invoiceLabel}",
                    Message = $"POD file {attachment.FileName} was uploaded for {invoiceLabel} ({customerDisplay}).",
                    Type = "Success",
                    Category = "POD",
                    EntityType = "Invoice",
                    EntityId = command.DocEntry.ToString(),
                    ActionUrl = "/pods",
                    TargetUserId = uploader.Id,
                    TargetUsername = targetUsername,
                    Data = notificationData
                };
            }
            else
            {
                notificationRequest = ModuleNotificationFactory.CreateBroadcastNotification(
                    $"POD Uploaded: {invoiceLabel}",
                    $"POD file {attachment.FileName} was uploaded for {invoiceLabel} ({customerDisplay}).",
                    "Success",
                    "POD",
                    "Invoice",
                    command.DocEntry.ToString(),
                    "/pods",
                    notificationData);
            }

            await notificationService.CreateNotificationAsync(
                notificationRequest,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish POD notification for invoice {DocEntry}", command.DocEntry);
        }

        // Best-effort: sync POD to SAP
        try
        {
            var (fileStream, _, _) = await documentService.DownloadAttachmentAsync(attachment.Id, cancellationToken);
            if (fileStream != null)
            {
                using (fileStream)
                {
                    if (invoiceInfo?.AttachmentEntry is int existingAttachmentEntry && existingAttachmentEntry > 0)
                    {
                        logger.LogInformation("Appending POD to existing SAP attachment {AttachmentEntry} for invoice {DocEntry}...",
                            existingAttachmentEntry, command.DocEntry);
                        await sapClient.AppendAttachmentToSAPAsync(existingAttachmentEntry, fileStream, fileName, cancellationToken);
                    }
                    else
                    {
                        logger.LogInformation("Uploading POD to SAP Attachments2 for invoice {DocEntry}...", command.DocEntry);
                        var absEntry = await sapClient.UploadAttachmentToSAPAsync(fileStream, fileName, cancellationToken);
                        await sapClient.LinkAttachmentToInvoiceAsync(command.DocEntry, absEntry, cancellationToken);
                        logger.LogInformation("POD synced to SAP for invoice {DocEntry} (AbsoluteEntry={AbsEntry})", command.DocEntry, absEntry);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to sync POD to SAP for invoice {DocEntry} (non-blocking)", command.DocEntry);
        }

        return attachment;
    }

    private static string BuildBusinessPartnerDisplay(string? cardCode, string? cardName)
    {
        var normalizedCode = cardCode?.Trim();
        var normalizedName = cardName?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return normalizedCode ?? "unknown customer";
        }

        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            return normalizedName;
        }

        return $"{normalizedCode} - {normalizedName}";
    }
}
