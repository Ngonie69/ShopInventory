using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Invoices.Commands.UploadPod;

public sealed class UploadPodHandler(
    ISAPServiceLayerClient sapClient,
    IDocumentService documentService,
    IAuthService authService,
    ILogger<UploadPodHandler> logger
) : IRequestHandler<UploadPodCommand, ErrorOr<DocumentAttachmentDto>>
{
    private static readonly HashSet<string> ExcludedPodCardCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "CIS006", "MAC009", "MAC006", "COR007", "COR006", "COR008",
        "VAN008", "VAN009", "VAN010", "VAN011", "VAN012", "VAN013",
        "VAN014", "VAN015", "VAN016", "VAN017", "VAN018", "VAN019", "VAN020",
        "STA040", "STA041", "STA042", "STA043", "STA044", "STA045", "STA046", "STA047", "STA048",
        "PRO030", "PRO031", "PRO032", "PRO033", "PRO034", "PRO035", "PRO036",
        "CAS004(FCA)", "DON004", "TEA006", "TEA007"
    };

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

        if (invoiceInfo != null && ExcludedPodCardCodes.Contains(invoiceInfo.CardCode ?? ""))
            return Errors.Invoice.PodExcluded(invoiceInfo.CardName, invoiceInfo.CardCode);

        var request = new UploadAttachmentRequest
        {
            EntityType = "Invoice",
            EntityId = command.DocEntry,
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
                    command.DocEntry, invoiceInfo.DocNum, invoiceInfo.CardCode, invoiceInfo.CardName, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not cache invoice info for DocEntry {DocEntry}", command.DocEntry);
            }
        }

        // Resolve user ID
        var userId = command.UserId;
        if (userId == null && !string.IsNullOrWhiteSpace(command.UploadedByUsername))
        {
            var user = await authService.GetUserByUsernameAsync(command.UploadedByUsername);
            userId = user?.Id;
        }

        // Prefix filename with POD_
        var fileName = command.FileName.StartsWith("POD", StringComparison.OrdinalIgnoreCase)
            ? command.FileName
            : $"POD_{command.FileName}";

        var attachment = await documentService.UploadAttachmentAsync(
            request, command.FileStream, fileName, command.ContentType, userId, cancellationToken);

        logger.LogInformation("POD uploaded for invoice {DocEntry} by user {UserId}", command.DocEntry, userId);

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
}
