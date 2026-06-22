using ErrorOr;
using MediatR;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNotes.Commands.CreateCreditNoteFromInvoice;

public sealed class CreateCreditNoteFromInvoiceHandler(
    ICreditNoteService creditNoteService,
    IAuditService auditService,
    ISender sender,
    IRevmaxClient revmaxClient,
    IIdempotencyRequestStore idempotencyRequestStore,
    ILogger<CreateCreditNoteFromInvoiceHandler> logger
) : IRequestHandler<CreateCreditNoteFromInvoiceCommand, ErrorOr<CreditNoteDto>>
{
    public async Task<ErrorOr<CreditNoteDto>> Handle(
        CreateCreditNoteFromInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        var clientRequestId = string.IsNullOrWhiteSpace(command.Request.ClientRequestId)
            ? null
            : command.Request.ClientRequestId.Trim();

        long? idempotencyRequestId = null;
        var releaseIdempotencyRequest = false;

        try
        {
            if (clientRequestId is not null)
            {
                var acquireResult = await idempotencyRequestStore.TryAcquireAsync<CreditNoteDto>(
                    "creditnotes.create-from-invoice",
                    clientRequestId,
                    command.Request,
                    cancellationToken);

                switch (acquireResult.Outcome)
                {
                    case IdempotencyAcquireOutcome.ReplayAvailable when acquireResult.Response is not null:
                        logger.LogWarning("Replaying credit note (from invoice {InvoiceId}) creation for idempotency key {Key}", command.InvoiceId, clientRequestId);
                        return acquireResult.Response;
                    case IdempotencyAcquireOutcome.InProgress:
                        return Errors.Idempotency.RequestInProgress("credit note creation");
                    case IdempotencyAcquireOutcome.RequestMismatch:
                        return Errors.Idempotency.RequestMismatch("credit note creation");
                    case IdempotencyAcquireOutcome.Acquired:
                        idempotencyRequestId = acquireResult.RequestId;
                        releaseIdempotencyRequest = true;
                        break;
                }
            }

            var lines = command.Request.Lines?.Select(l => new CreateCreditNoteLineRequest
            {
                ItemCode = l.ItemCode ?? "",
                ItemDescription = l.ItemDescription,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                DiscountPercent = l.DiscountPercent,
                TaxPercent = l.TaxPercent,
                WarehouseCode = l.WarehouseCode,
                ReturnReason = l.ReturnReason,
                OriginalInvoiceLineId = l.OriginalInvoiceLineId
            }).ToList() ?? new List<CreateCreditNoteLineRequest>();

            var creditNote = await creditNoteService.CreateFromInvoiceAsync(
                command.InvoiceId, lines, command.Request.Reason ?? "", command.UserId, cancellationToken);

            // The SAP credit note now exists. Complete idempotency immediately so any retry replays
            // this result instead of posting a duplicate, even if a later step below fails.
            if (idempotencyRequestId.HasValue)
            {
                try
                {
                    await idempotencyRequestStore.CompleteAsync(idempotencyRequestId.Value, creditNote, cancellationToken);
                    releaseIdempotencyRequest = false;
                }
                catch (Exception completeException)
                {
                    logger.LogWarning(completeException, "Failed to persist credit note (from invoice) idempotency completion for request {RequestId}", idempotencyRequestId.Value);
                }
            }

            await CreditNoteFiscalTransactionSync.SyncAsync(
                creditNote,
                revmaxClient,
                sender,
                logger,
                command.UserId.ToString(),
                cancellationToken);

            try { await auditService.LogAsync(AuditActions.CreateCreditNote, "CreditNote", creditNote.Id.ToString(), $"Credit note created from invoice {command.InvoiceId}", true); } catch { }
            return creditNote;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.CreditNote.InvalidOperation(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating credit note from invoice {InvoiceId}", command.InvoiceId);
            return Errors.CreditNote.CreationFailed(ex.Message);
        }
        finally
        {
            if (releaseIdempotencyRequest && idempotencyRequestId.HasValue)
            {
                try
                {
                    await idempotencyRequestStore.ReleaseAsync(idempotencyRequestId.Value, cancellationToken);
                }
                catch (Exception releaseException)
                {
                    logger.LogWarning(releaseException, "Failed to release credit note (from invoice) idempotency request {RequestId}", idempotencyRequestId.Value);
                }
            }
        }
    }
}
