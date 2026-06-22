using ErrorOr;
using MediatR;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNotes.Commands.CreateCreditNote;

public sealed class CreateCreditNoteHandler(
    ICreditNoteService creditNoteService,
    IAuditService auditService,
    ISender sender,
    IRevmaxClient revmaxClient,
    IIdempotencyRequestStore idempotencyRequestStore,
    ILogger<CreateCreditNoteHandler> logger
) : IRequestHandler<CreateCreditNoteCommand, ErrorOr<CreditNoteDto>>
{
    public async Task<ErrorOr<CreditNoteDto>> Handle(
        CreateCreditNoteCommand command,
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
                    "creditnotes.create",
                    clientRequestId,
                    command.Request,
                    cancellationToken);

                switch (acquireResult.Outcome)
                {
                    case IdempotencyAcquireOutcome.ReplayAvailable when acquireResult.Response is not null:
                        logger.LogWarning("Replaying credit note creation for idempotency key {Key}", clientRequestId);
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

            var creditNote = await creditNoteService.CreateAsync(command.Request, command.UserId, cancellationToken);

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
                    logger.LogWarning(completeException, "Failed to persist credit note idempotency completion for request {RequestId}", idempotencyRequestId.Value);
                }
            }

            await CreditNoteFiscalTransactionSync.SyncAsync(
                creditNote,
                revmaxClient,
                sender,
                logger,
                command.UserId.ToString(),
                cancellationToken);

            try { await auditService.LogAsync(AuditActions.CreateCreditNote, "CreditNote", creditNote.Id.ToString(), $"Credit note created for {command.Request.CardCode}", true); } catch { }
            return creditNote;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating credit note");
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
                    logger.LogWarning(releaseException, "Failed to release credit note idempotency request {RequestId}", idempotencyRequestId.Value);
                }
            }
        }
    }
}
