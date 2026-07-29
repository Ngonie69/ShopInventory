using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNotes.Commands.DuplicateCancelledCreditNotes;

public sealed class DuplicateCancelledCreditNotesHandler(
    ISAPServiceLayerClient sapClient,
    ICreditNoteService creditNoteService,
    ApplicationDbContext dbContext,
    IAuditService auditService,
    ILogger<DuplicateCancelledCreditNotesHandler> logger
) : IRequestHandler<DuplicateCancelledCreditNotesCommand, ErrorOr<DuplicateCancelledCreditNotesResult>>
{
    public async Task<ErrorOr<DuplicateCancelledCreditNotesResult>> Handle(
        DuplicateCancelledCreditNotesCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var docEntries = command.Request.CreditNoteDocEntries
                .Where(docEntry => docEntry > 0)
                .Distinct()
                .ToList();

            var results = new List<DuplicateCancelledCreditNoteResultItem>();

            foreach (var docEntry in docEntries)
            {
                results.Add(await DuplicateOneAsync(docEntry, command.UserId, command.Request.Reason, cancellationToken));
            }

            var successCount = results.Count(result => result.Success);
            return new DuplicateCancelledCreditNotesResult(
                docEntries.Count,
                successCount,
                results.Count - successCount,
                results);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during cancelled credit note duplication");
            return Errors.CreditNote.DuplicationFailed("Credit note duplication failed. Please try again.");
        }
    }

    private async Task<DuplicateCancelledCreditNoteResultItem> DuplicateOneAsync(
        int docEntry,
        Guid userId,
        string? reason,
        CancellationToken cancellationToken)
    {
        SAPCreditNote? originalCreditNote = null;

        try
        {
            originalCreditNote = await sapClient.GetCreditNoteByDocEntryAsync(docEntry, cancellationToken);
            if (originalCreditNote is null)
            {
                return Failed(docEntry, null, $"SAP-CN-{docEntry}", "NotFound", "Credit note was not found in SAP.");
            }

            if (!IsCancelled(originalCreditNote))
            {
                return Failed(
                    docEntry,
                    originalCreditNote.DocNum,
                    GetCreditNoteNumber(originalCreditNote),
                    "NotCancelled",
                    "Only cancelled credit notes can be duplicated.");
            }

            var lines = BuildDuplicateLines(originalCreditNote);
            if (lines.Count == 0)
            {
                return Failed(
                    docEntry,
                    originalCreditNote.DocNum,
                    GetCreditNoteNumber(originalCreditNote),
                    "NoLines",
                    "Credit note has no valid lines to duplicate.");
            }

            var localCreditNote = await dbContext.CreditNotes
                .AsNoTracking()
                .FirstOrDefaultAsync(creditNote => creditNote.SAPDocEntry == docEntry, cancellationToken);

            var request = new CreateCreditNoteRequest
            {
                CardCode = originalCreditNote.CardCode ?? string.Empty,
                CardName = originalCreditNote.CardName,
                Type = localCreditNote?.Type ?? CreditNoteType.Return,
                OriginalInvoiceId = localCreditNote?.OriginalInvoiceId,
                OriginalInvoiceDocEntry = GetOriginalInvoiceDocEntry(originalCreditNote) ?? localCreditNote?.OriginalInvoiceDocEntry,
                Reason = BuildDuplicateReason(originalCreditNote, reason),
                Comments = originalCreditNote.Comments,
                Currency = originalCreditNote.DocCurrency,
                RestockItems = localCreditNote?.RestockItems ?? true,
                RestockWarehouseCode = localCreditNote?.RestockWarehouseCode,
                Lines = lines
            };

            var duplicated = await creditNoteService.CreateAsync(request, userId, cancellationToken);

            await TryAuditAsync(userId, originalCreditNote, duplicated, reason);

            return new DuplicateCancelledCreditNoteResultItem(
                originalCreditNote.DocEntry,
                originalCreditNote.DocNum,
                GetCreditNoteNumber(originalCreditNote),
                duplicated.Id,
                duplicated.SAPDocEntry,
                duplicated.SAPDocNum,
                duplicated.CreditNoteNumber,
                true,
                "Duplicated",
                "Credit note duplicated.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to duplicate cancelled SAP credit note {DocEntry}", docEntry);
            return Failed(
                docEntry,
                originalCreditNote?.DocNum,
                originalCreditNote is null ? $"SAP-CN-{docEntry}" : GetCreditNoteNumber(originalCreditNote),
                "Failed",
                "SAP rejected the duplicate credit note.");
        }
    }

    private static List<CreateCreditNoteLineRequest> BuildDuplicateLines(SAPCreditNote originalCreditNote)
    {
        return originalCreditNote.DocumentLines?
            .Where(line => !string.IsNullOrWhiteSpace(line.ItemCode) && Math.Abs(line.Quantity) > 0)
            .Select((line, index) => new CreateCreditNoteLineRequest
            {
                ItemCode = line.ItemCode ?? string.Empty,
                ItemDescription = line.ItemDescription,
                Quantity = Math.Abs(line.Quantity),
                UnitPrice = line.UnitPrice != 0 ? line.UnitPrice : line.Price,
                DiscountPercent = line.DiscountPercent ?? 0,
                TaxPercent = 0,
                WarehouseCode = line.WarehouseCode,
                OriginalInvoiceLineId = line.BaseType == 13 ? line.BaseLine ?? index : null
            })
            .ToList() ?? new List<CreateCreditNoteLineRequest>();
    }

    private async Task TryAuditAsync(
        Guid userId,
        SAPCreditNote originalCreditNote,
        CreditNoteDto duplicated,
        string? reason)
    {
        try
        {
            var details = string.IsNullOrWhiteSpace(reason)
                ? $"Duplicated cancelled credit note {GetCreditNoteNumber(originalCreditNote)} as {duplicated.SAPDocNum}"
                : $"Duplicated cancelled credit note {GetCreditNoteNumber(originalCreditNote)} as {duplicated.SAPDocNum}. Reason: {reason}";

            await auditService.LogAsync(
                AuditActions.DuplicateCancelledCreditNotes,
                "CreditNote",
                duplicated.Id.ToString(),
                details,
                true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to audit cancelled credit note duplication for {DocEntry} by {UserId}",
                originalCreditNote.DocEntry, userId);
        }
    }

    private static int? GetOriginalInvoiceDocEntry(SAPCreditNote creditNote) =>
        creditNote.BaseEntry
        ?? creditNote.DocumentLines?
            .FirstOrDefault(line => line.BaseType == 13 && line.BaseEntry.HasValue)
            ?.BaseEntry;

    private static string BuildDuplicateReason(SAPCreditNote creditNote, string? reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? $"Duplicated from cancelled credit note {GetCreditNoteNumber(creditNote)}"
            : reason.Trim();

    private static bool IsCancelled(SAPCreditNote creditNote) =>
        string.Equals(creditNote.Cancelled, "tYES", StringComparison.OrdinalIgnoreCase);

    private static string GetCreditNoteNumber(SAPCreditNote creditNote) =>
        creditNote.DocNum > 0 ? $"SAP-CN-{creditNote.DocNum}" : $"SAP-CN-{creditNote.DocEntry}";

    private static DuplicateCancelledCreditNoteResultItem Failed(
        int docEntry,
        int? docNum,
        string creditNoteNumber,
        string status,
        string message) =>
        new(docEntry, docNum, creditNoteNumber, null, null, null, null, false, status, message);
}