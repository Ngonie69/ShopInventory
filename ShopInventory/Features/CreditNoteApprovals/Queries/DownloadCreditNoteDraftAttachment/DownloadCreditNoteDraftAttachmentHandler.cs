using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNoteApprovals.Queries.DownloadCreditNoteDraftAttachment;

public sealed class DownloadCreditNoteDraftAttachmentHandler(
    ISAPServiceLayerClient sap,
    IOptions<SAPSettings> sapSettings,
    IOptions<CreditNoteApprovalSettings> approvalSettings,
    ILogger<DownloadCreditNoteDraftAttachmentHandler> logger)
    : IRequestHandler<DownloadCreditNoteDraftAttachmentQuery, ErrorOr<SapAttachmentDownload>>
{
    public async Task<ErrorOr<SapAttachmentDownload>> Handle(
        DownloadCreditNoteDraftAttachmentQuery query,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
        {
            return Errors.CreditNoteApproval.SapDisabled;
        }

        var request = await sap.GetApprovalRequestAsync(query.Code, cancellationToken);
        if (request is null || !string.Equals(request.ObjectType, SapObjectTypes.CreditNote, StringComparison.Ordinal))
        {
            return Errors.CreditNoteApproval.NotFound(query.Code);
        }

        if (request.DraftEntry is not int draftEntry)
        {
            return Errors.CreditNoteApproval.AttachmentNotFound(query.Code, query.LineNum);
        }

        // The header read filters on the credit memo object code, so any other kind of draft is absent.
        var draft = (await sap.GetCreditNoteDraftsAsync([draftEntry], cancellationToken)).FirstOrDefault();
        if (draft is null)
        {
            return Errors.CreditNoteApproval.DraftMissing(draftEntry);
        }

        if (draft.AttachmentEntry is not int attachmentEntry || attachmentEntry <= 0)
        {
            return Errors.CreditNoteApproval.AttachmentNotFound(query.Code, query.LineNum);
        }

        try
        {
            var attachment = await sap.GetAttachmentAsync(attachmentEntry, cancellationToken);
            var line = attachment?.Attachments2_Lines?.FirstOrDefault(candidate => candidate.LineNum == query.LineNum);
            if (line is null || string.IsNullOrWhiteSpace(line.FullFileName))
            {
                return Errors.CreditNoteApproval.AttachmentNotFound(query.Code, query.LineNum);
            }

            var fromShare = approvalSettings.Value.ReadsAttachmentsFromShare;
            var download = fromShare
                ? await sap.ReadAttachmentFromShareAsync(line, cancellationToken)
                : await sap.DownloadAttachmentAsync(attachmentEntry, line.FullFileName, cancellationToken);

            if (download is not null)
            {
                return download;
            }

            // The line is on the document — SAP just did not hand over the bytes. Saying "no such
            // attachment" here would deny a file the drawer is listing by name.
            logger.LogWarning(
                "SAP listed '{FileName}' on approval request {Code} but returned no content for it",
                line.FullFileName,
                query.Code);

            return Errors.CreditNoteApproval.AttachmentUnavailable(
                fromShare
                    ? $"'{line.FullFileName}' is not in the SAP attachments folder."
                    : $"SAP returned no content for '{line.FullFileName}'.");
        }
        catch (SapRequestRejectedException rejected)
        {
            logger.LogWarning(rejected, "SAP refused the attachment read for approval request {Code} line {LineNum}", query.Code, query.LineNum);
            return Errors.CreditNoteApproval.AttachmentUnavailable(rejected.SapMessage);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "The attachment for approval request {Code} line {LineNum} could not be read", query.Code, query.LineNum);
            return Errors.CreditNoteApproval.AttachmentUnavailable(exception.Message);
        }
    }
}
