using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNotes.Commands.BulkCancelCreditNotes;

public sealed class BulkCancelCreditNotesHandler(
    ISAPServiceLayerClient sapClient,
    ApplicationDbContext dbContext,
    IAuditService auditService,
    ILogger<BulkCancelCreditNotesHandler> logger
) : IRequestHandler<BulkCancelCreditNotesCommand, ErrorOr<BulkCancelCreditNotesResult>>
{
    public async Task<ErrorOr<BulkCancelCreditNotesResult>> Handle(
        BulkCancelCreditNotesCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var docEntries = command.Request.CreditNoteDocEntries
                .Where(docEntry => docEntry > 0)
                .Distinct()
                .ToList();

            var results = new List<BulkCancelCreditNoteResultItem>();

            foreach (var docEntry in docEntries)
            {
                results.Add(await CancelOneAsync(docEntry, command.UserId, command.Request.Reason, cancellationToken));
            }

            var successCount = results.Count(result => result.Success);
            return new BulkCancelCreditNotesResult(
                docEntries.Count,
                successCount,
                results.Count - successCount,
                results);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during bulk credit note cancellation");
            return Errors.CreditNote.BulkCancellationFailed("Bulk cancellation failed. Please try again.");
        }
    }

    private async Task<BulkCancelCreditNoteResultItem> CancelOneAsync(
        int docEntry,
        Guid userId,
        string? reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var creditNote = await sapClient.GetCreditNoteByDocEntryAsync(docEntry, cancellationToken);
            if (creditNote is null)
            {
                return new BulkCancelCreditNoteResultItem(
                    docEntry,
                    null,
                    $"SAP-CN-{docEntry}",
                    false,
                    "NotFound",
                    "Credit note was not found in SAP.");
            }

            if (IsCancelled(creditNote))
            {
                await MarkLocalCreditNoteCancelledAsync(docEntry, cancellationToken);
                return new BulkCancelCreditNoteResultItem(
                    docEntry,
                    creditNote.DocNum,
                    GetCreditNoteNumber(creditNote),
                    true,
                    "AlreadyCancelled",
                    "Credit note was already cancelled.");
            }

            await sapClient.CancelCreditNoteAsync(docEntry, cancellationToken);
            await MarkLocalCreditNoteCancelledAsync(docEntry, cancellationToken);

            await TryAuditAsync(userId, creditNote, reason, cancellationToken);

            return new BulkCancelCreditNoteResultItem(
                docEntry,
                creditNote.DocNum,
                GetCreditNoteNumber(creditNote),
                true,
                "Cancelled",
                "Credit note cancelled.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to cancel SAP credit note {DocEntry}", docEntry);
            return new BulkCancelCreditNoteResultItem(
                docEntry,
                null,
                $"SAP-CN-{docEntry}",
                false,
                "Failed",
                "SAP rejected the cancellation request.");
        }
    }

    private async Task MarkLocalCreditNoteCancelledAsync(int sapDocEntry, CancellationToken cancellationToken)
    {
        var localCreditNote = await dbContext.CreditNotes
            .FirstOrDefaultAsync(creditNote => creditNote.SAPDocEntry == sapDocEntry, cancellationToken);

        if (localCreditNote is null)
        {
            return;
        }

        localCreditNote.Status = CreditNoteStatus.Cancelled;
        localCreditNote.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task TryAuditAsync(
        Guid userId,
        SAPCreditNote creditNote,
        string? reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var details = string.IsNullOrWhiteSpace(reason)
                ? $"Credit note {GetCreditNoteNumber(creditNote)} cancelled by bulk action"
                : $"Credit note {GetCreditNoteNumber(creditNote)} cancelled by bulk action. Reason: {reason}";

            await auditService.LogAsync(
                AuditActions.BulkCancelCreditNotes,
                "CreditNote",
                creditNote.DocEntry.ToString(),
                details,
                true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to audit bulk credit note cancellation for {DocEntry} by {UserId}",
                creditNote.DocEntry, userId);
        }
    }

    private static bool IsCancelled(SAPCreditNote creditNote) =>
        string.Equals(creditNote.Cancelled, "tYES", StringComparison.OrdinalIgnoreCase);

    private static string GetCreditNoteNumber(SAPCreditNote creditNote) =>
        creditNote.DocNum > 0 ? $"SAP-CN-{creditNote.DocNum}" : $"SAP-CN-{creditNote.DocEntry}";
}