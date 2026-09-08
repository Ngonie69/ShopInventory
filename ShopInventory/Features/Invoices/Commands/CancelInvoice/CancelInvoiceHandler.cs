using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Controllers;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.CreditNotes.Commands.CreateCreditNoteFromInvoice;
using ShopInventory.Features.CreditNotes.Queries.GetCreditNoteReasons;
using ShopInventory.Hubs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.Invoices.Commands.CancelInvoice;

/// <summary>
/// Cancels a posted invoice by raising the credit note that reverses it in full.
/// </summary>
/// <remarks>
/// There is no undo for a posted A/R invoice in this landscape: the document has been fiscalised
/// and ZIMRA has seen it, so it cannot be withdrawn — only answered with a credit note that says
/// why. The whole invoice is reversed, every line at its full quantity, because a cancellation is
/// the sale being called off rather than part of it coming back; a partial credit is the existing
/// credit note flow and stays there.
///
/// The posting itself is delegated to <see cref="CreateCreditNoteFromInvoiceCommand"/> rather than
/// repeated here. That path already carries the things a cancellation must not lose — the
/// idempotency guard that stops a retry posting a second credit note, batch and serial numbers
/// lifted from the invoice being reversed, and the fiscalisation of the credit note afterwards.
/// </remarks>
public sealed class CancelInvoiceHandler(
    ISAPServiceLayerClient sapClient,
    ApplicationDbContext dbContext,
    ISender sender,
    IHubContext<NotificationHub> hubContext,
    INotificationService notificationService,
    IAuditService auditService,
    ILogger<CancelInvoiceHandler> logger
) : IRequestHandler<CancelInvoiceCommand, ErrorOr<CancelInvoiceResult>>
{
    public async Task<ErrorOr<CancelInvoiceResult>> Handle(
        CancelInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        var reasonResult = await ResolveReasonAsync(command.Reason.Trim(), cancellationToken);
        if (reasonResult.IsError)
        {
            return reasonResult.Errors;
        }

        var resolvedReason = reasonResult.Value;

        Invoice? invoice;
        try
        {
            invoice = await sapClient.GetInvoiceByDocEntryAsync(command.DocEntry, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read invoice {DocEntry} from SAP before cancelling it", command.DocEntry);
            return Errors.Invoice.RetrievalFailed(ex.Message);
        }

        if (invoice is null)
        {
            return Errors.Invoice.NotFound(command.DocEntry);
        }

        // SAP's own cancellation flag. An invoice cancelled inside B1 has already been answered by a
        // reversing document, so raising another credit note would credit the customer twice.
        if (string.Equals(invoice.Cancelled, "tYES", StringComparison.OrdinalIgnoreCase))
        {
            return Errors.Invoice.AlreadyCancelled(invoice.DocNum);
        }

        if (invoice.DocumentLines is not { Count: > 0 })
        {
            return Errors.Invoice.NoLinesToCancel(invoice.DocNum);
        }

        var request = new CreateCreditNoteFromInvoiceApiRequest
        {
            Reason = BuildHeaderReason(resolvedReason, command.Comments),
            ClientRequestId = command.ClientRequestId,
            Lines = invoice.DocumentLines
                .Select(line => new CreditNoteLineApiRequest
                {
                    ItemCode = line.ItemCode,
                    ItemDescription = line.ItemDescription,
                    Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice,
                    DiscountPercent = line.DiscountPercent,
                    WarehouseCode = line.WarehouseCode,
                    // The reason SAP reads. It goes on every line because U_Reasons is a line field:
                    // a cancellation with it set on only some lines reports as a partial return.
                    ReturnReason = resolvedReason.Value,
                    OriginalInvoiceLineId = line.LineNum
                })
                .ToList()
        };

        var creditNoteResult = await sender.Send(
            new CreateCreditNoteFromInvoiceCommand(command.DocEntry, request, command.UserId),
            cancellationToken);

        if (creditNoteResult.IsError)
        {
            await LogAuditAsync(invoice, resolvedReason, null, false);
            return creditNoteResult.Errors;
        }

        var creditNote = creditNoteResult.Value;

        await MarkAsCancellationAsync(creditNote.Id, cancellationToken);

        var notifiedWarehouses = await AnnounceAsync(invoice, creditNote, resolvedReason, command, cancellationToken);

        await LogAuditAsync(invoice, resolvedReason, creditNote.CreditNoteNumber, true);

        return new CancelInvoiceResult(
            invoice.DocEntry,
            invoice.DocNum,
            creditNote.Id,
            creditNote.CreditNoteNumber,
            creditNote.SAPDocEntry,
            creditNote.SAPDocNum,
            creditNote.DocTotal,
            creditNote.Currency,
            resolvedReason.Value,
            notifiedWarehouses);
    }

    /// <summary>
    /// Checks the submitted reason against the list the running company database defines.
    /// </summary>
    /// <remarks>
    /// Worth doing before anything is posted. <c>U_Reasons</c> is a valid-values field, so SAP
    /// refuses a value it does not know — but it refuses it at the end, after the credit note has
    /// been built, with a Service Layer message that names neither the field nor the alternatives.
    /// A company database that defines no reason field at all accepts free text, because there is
    /// then no list to be wrong about.
    /// </remarks>
    private async Task<ErrorOr<CreditNoteReasonOption>> ResolveReasonAsync(string reason, CancellationToken cancellationToken)
    {
        var reasonsResult = await sender.Send(new GetCreditNoteReasonsQuery(), cancellationToken);
        if (reasonsResult.IsError)
        {
            return reasonsResult.Errors;
        }

        var reasons = reasonsResult.Value.Reasons;
        if (reasons.Count == 0)
        {
            return new CreditNoteReasonOption(reason, reason);
        }

        var match = reasons.FirstOrDefault(option =>
            string.Equals(option.Value, reason, StringComparison.OrdinalIgnoreCase));

        return match is null
            ? Errors.Invoice.UnknownCancellationReason(reason, reasons.Select(option => option.Value))
            : match;
    }

    /// <summary>
    /// The header text, which is a sentence for a person rather than the code SAP matches on.
    /// </summary>
    private static string BuildHeaderReason(CreditNoteReasonOption reason, string? comments)
    {
        var header = $"Invoice cancelled: {reason.Description}";
        return string.IsNullOrWhiteSpace(comments) ? header : $"{header}. {comments.Trim()}";
    }

    /// <summary>
    /// Records that this credit note withdrew an invoice rather than took stock back.
    /// </summary>
    /// <remarks>
    /// Written after the fact because the posting path builds every credit note as a
    /// <see cref="CreditNoteType.Return"/>, and widening that path to carry a type would touch every
    /// caller of it for the benefit of this one. A failure here leaves a credit note that is real,
    /// posted and fiscalised but filed as a return — worth a warning, not worth failing a
    /// cancellation that SAP has already accepted.
    /// </remarks>
    private async Task MarkAsCancellationAsync(int creditNoteId, CancellationToken cancellationToken)
    {
        try
        {
            var entity = await dbContext.CreditNotes
                .FirstOrDefaultAsync(note => note.Id == creditNoteId, cancellationToken);

            if (entity is null)
            {
                return;
            }

            entity.Type = CreditNoteType.Cancellation;
            entity.UpdatedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Credit note {CreditNoteId} was posted but could not be marked as a cancellation", creditNoteId);
        }
    }

    /// <summary>
    /// Tells the tills, and everyone who works invoices, that the receipt is void.
    /// </summary>
    /// <returns>The warehouses a live push was addressed to.</returns>
    private async Task<IReadOnlyList<string>> AnnounceAsync(
        Invoice invoice,
        CreditNoteDto creditNote,
        CreditNoteReasonOption reason,
        CancelInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        // Which till issued the receipt. A consolidated end-of-day invoice covers a whole day of
        // sales, and can cover more than one warehouse, so this is a set rather than a single till.
        var tillSales = await dbContext.DesktopSales
            .AsNoTracking()
            .Where(sale => sale.SapDocEntry == invoice.DocEntry)
            .Select(sale => new { sale.ExternalReferenceId, sale.WarehouseCode })
            .ToListAsync(cancellationToken);

        var warehouses = tillSales
            .Select(sale => sale.WarehouseCode)
            .Concat(invoice.DocumentLines?.Select(line => line.WarehouseCode) ?? [])
            .Where(warehouse => !string.IsNullOrWhiteSpace(warehouse))
            .Select(warehouse => warehouse!.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var payload = new
        {
            InvoiceDocEntry = invoice.DocEntry,
            InvoiceDocNum = invoice.DocNum,
            invoice.CardCode,
            invoice.CardName,
            Currency = invoice.DocCurrency,
            invoice.DocTotal,
            CreditNoteId = creditNote.Id,
            creditNote.CreditNoteNumber,
            CreditNoteDocEntry = creditNote.SAPDocEntry,
            CreditNoteDocNum = creditNote.SAPDocNum,
            Reason = reason.Value,
            ReasonDescription = reason.Description,
            command.Comments,
            // What a till matches against its own records: the reference it sent when it made the
            // sale. Empty for an invoice that did not come from a till.
            TillReferences = tillSales.Select(sale => sale.ExternalReferenceId).ToList(),
            Warehouses = warehouses,
            CancelledAtUtc = DateTime.UtcNow
        };

        try
        {
            if (warehouses.Count > 0)
            {
                await hubContext.Clients
                    .Groups(warehouses.Select(NotificationHub.WarehouseGroup).ToList())
                    .SendAsync("InvoiceCancelled", payload, cancellationToken);
            }
            else
            {
                logger.LogWarning(
                    "Invoice {DocNum} was cancelled but could not be traced to a warehouse; no till was pushed the cancellation",
                    invoice.DocNum);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to push the cancellation of invoice {DocNum} to the tills", invoice.DocNum);
        }

        try
        {
            await notificationService.CreateNotificationAsync(
                InvoiceCancellationNotificationFactory.Create(invoice, creditNote, reason, command.Comments),
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to raise the cancellation notification for invoice {DocNum}", invoice.DocNum);
        }

        return warehouses;
    }

    private async Task LogAuditAsync(
        Invoice invoice,
        CreditNoteReasonOption reason,
        string? creditNoteNumber,
        bool succeeded)
    {
        try
        {
            var details = succeeded
                ? $"Invoice {invoice.DocNum} cancelled ({reason.Value}); credit note {creditNoteNumber}"
                : $"Invoice {invoice.DocNum} cancellation failed ({reason.Value})";

            await auditService.LogAsync(
                AuditActions.CancelInvoice,
                "Invoice",
                invoice.DocEntry.ToString(),
                details,
                succeeded);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write the audit entry for cancelling invoice {DocNum}", invoice.DocNum);
        }
    }
}
