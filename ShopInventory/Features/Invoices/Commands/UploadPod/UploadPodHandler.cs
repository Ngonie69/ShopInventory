using ErrorOr;
using MediatR;
using ShopInventory.Common.Pods;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.Documents;
using ShopInventory.Features.Notifications;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Invoices.Commands.UploadPod;

public sealed class UploadPodHandler(
    ISAPServiceLayerClient sapClient,
    DocumentAttachmentAccessService attachmentAccessService,
    IDocumentService documentService,
    IAuthService authService,
    IAuditService auditService,
    INotificationService notificationService,
    ILogger<UploadPodHandler> logger
) : IRequestHandler<UploadPodCommand, ErrorOr<DocumentAttachmentDto>>
{
    /// <summary>
    /// How long after a driver's POD a second one from the same driver on the same invoice is read
    /// as the same submission rather than a new one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The guards above this one are both better, and both miss this case. The caller's reference
    /// only works when the caller sends one — it caught 1 of 48 uploads on 2026-08-06. Content
    /// hashing does not depend on the caller behaving, but it does depend on the bytes repeating,
    /// and the mobile app produces a differently encoded file per tap: its hash guard matched
    /// nothing at all that day while seven invoices took a second POD 2 to 10 seconds after the
    /// first, each one a second stored file, a second bell entry and a second push.
    /// </para>
    /// <para>
    /// So the key here is time, which is the one thing left that the client cannot vary. Fifteen
    /// seconds is chosen against what a person can physically do rather than against the observed
    /// gaps: a driver cannot line up and photograph a second page that fast, so anything inside it
    /// is the same photo arriving twice. A real second page lands well outside — the deliberate
    /// re-upload in that day's log came nine minutes later, and is untouched by this.
    /// </para>
    /// <para>
    /// Only applied when the caller sends no reference of its own. A client that mints one per
    /// attempt is making a claim about which submissions are distinct, and that claim is better
    /// evidence than this clock.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan DoubleSubmitWindow = TimeSpan.FromSeconds(15);

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

        if (invoiceInfo != null && PodExclusions.IsExcluded(invoiceInfo.CardCode, invoiceInfo.CardName))
            return Errors.Invoice.PodExcluded(invoiceInfo.CardName ?? "", invoiceInfo.CardCode ?? "");

        var accessResult = await attachmentAccessService.AuthorizeEntityAccessAsync(
            "Invoice",
            command.DocEntry,
            true,
            cancellationToken);

        if (accessResult.IsError)
        {
            return accessResult.Errors;
        }

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
        else if (userId is Guid uploaderId)
        {
            var recentUpload = await documentService.FindRecentAttachmentByUploaderAsync(
                request.EntityType,
                request.EntityId,
                uploaderId,
                DoubleSubmitWindow,
                cancellationToken);

            if (recentUpload is not null)
            {
                logger.LogInformation(
                    "Skipping POD re-submission for invoice {DocEntry} by user {UserId} within {WindowSeconds}s; reusing attachment {AttachmentId}",
                    command.DocEntry,
                    uploaderId,
                    DoubleSubmitWindow.TotalSeconds,
                    recentUpload.Id);
                return recentUpload;
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
            var notificationTitle = $"POD Uploaded: {invoiceLabel}";
            var notificationMessage = $"POD file {attachment.FileName} was uploaded for {invoiceLabel} ({customerDisplay}).";

            if (uploader is not null &&
                string.Equals(uploader.Role, "Driver", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(targetUsername))
            {
                await notificationService.CreateNotificationAsync(
                    new CreateNotificationRequest
                    {
                        Title = notificationTitle,
                        Message = notificationMessage,
                        Type = "Success",
                        Category = "POD",
                        EntityType = "Invoice",
                        EntityId = command.DocEntry.ToString(),
                        ActionUrl = "/pods",
                        TargetUserId = uploader.Id,
                        TargetUsername = targetUsername,
                        Data = notificationData
                    },
                    cancellationToken);
            }
            else
            {
                // One broadcast row, not one row per role. A PodOperator upload used to fan out
                // across every non-Driver PodAudienceRole, so a single POD wrote four rows and a
                // busy depot buried every other module's notifications in the bell. The audience is
                // unchanged: CreateNotificationAsync resolves a broadcast to
                // GetBroadcastAudienceRoles("POD", "/pods") — the same four roles — for both the
                // SignalR groups and the push fan-out, and the visibility query admits a broadcast
                // to exactly those roles. Drivers were never in that set, so the old non-Driver
                // filter was already a no-op.
                await notificationService.CreateNotificationAsync(
                    ModuleNotificationFactory.CreateBroadcastNotification(
                        notificationTitle,
                        notificationMessage,
                        "Success",
                        "POD",
                        "Invoice",
                        command.DocEntry.ToString(),
                        "/pods",
                        notificationData),
                    cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish POD notification for invoice {DocEntry}", command.DocEntry);
        }

        logger.LogInformation(
            "Skipping SAP POD sync for invoice {DocEntry}; POD attachments remain stored in the application only.",
            command.DocEntry);

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
