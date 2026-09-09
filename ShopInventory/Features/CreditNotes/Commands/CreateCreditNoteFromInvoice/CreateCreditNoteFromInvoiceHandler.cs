using ErrorOr;
using MediatR;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Common.Errors;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.CreditNotes.Commands.CreateCreditNoteFromInvoice;

public sealed class CreateCreditNoteFromInvoiceHandler(
    ICreditNoteService creditNoteService,
    IAuditService auditService,
    ISender sender,
    IFiscalisationApiClient fiscalisationClient,
    IFiscalDeviceConfigCache fiscalConfigCache,
    IIdempotencyRequestStore idempotencyRequestStore,
    INotificationService notificationService,
    IOptions<SecuritySettings> securitySettings,
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

        // Whether the work that can reach SAP has started. Past that point a credit note may exist
        // whatever the failure looks like from here, so the catches below must not treat an error as
        // proof that nothing was created.
        var postIssued = false;

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
                    {
                        // A claim nobody completed. Since a claim is now kept when a post's outcome
                        // is unknown, this can mean an attempt that reached SAP and never learned
                        // what became of it — so refusing forever would strand the credit note.
                        //
                        // Past the grace window the claim is taken over and the request runs again.
                        // That is safe because CreateAsync asks SAP about the reference before it
                        // posts anything: if SAP holds the credit note it is adopted, and if it does
                        // not, the earlier post never landed. Inside the window that question has no
                        // trustworthy answer — a committed credit note may simply not be visible yet
                        // — so the caller waits rather than risking a second ZIMRA credit receipt.
                        var issuedBefore = DateTime.UtcNow.AddMinutes(
                            -Math.Max(0, securitySettings.Value.IdempotencyUnresolvedPostGraceMinutes));

                        if (acquireResult.RequestId is not { } unfinishedRequestId
                            || !await idempotencyRequestStore.TryTakeOverAsync(
                                unfinishedRequestId, issuedBefore, cancellationToken))
                        {
                            return Errors.Idempotency.PostOutcomeUnknown(
                                "credit note creation",
                                SaleReferenceNamespace.ForCreditNoteRequest(clientRequestId));
                        }

                        logger.LogWarning(
                            "Took over an unfinished credit note claim for key {Key}; SAP is asked about it before anything is posted.",
                            clientRequestId);

                        idempotencyRequestId = unfinishedRequestId;
                        releaseIdempotencyRequest = true;
                        break;
                    }
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

            postIssued = true;
            var creditNote = await creditNoteService.CreateFromInvoiceAsync(
                command.InvoiceId, lines, command.Request.Reason ?? "", command.UserId, clientRequestId, cancellationToken);

            // The SAP credit note now exists. Complete idempotency immediately so any retry replays
            // this result instead of posting a duplicate, even if a later step below fails.
            if (idempotencyRequestId.HasValue)
            {
                try
                {
                    await idempotencyRequestStore.CompleteAsync(idempotencyRequestId.Value, creditNote, CancellationToken.None);
                    releaseIdempotencyRequest = false;
                }
                catch (Exception completeException)
                {
                    logger.LogWarning(completeException, "Failed to persist credit note (from invoice) idempotency completion for request {RequestId}", idempotencyRequestId.Value);
                }
            }

            await CreditNoteFiscalTransactionSync.SyncAsync(
                creditNote,
                fiscalisationClient,
                fiscalConfigCache,
                sender,
                logger,
                command.UserId.ToString(),
                cancellationToken);

            try { await auditService.LogAsync(AuditActions.CreateCreditNote, "CreditNote", creditNote.Id.ToString(), $"Credit note created from invoice {command.InvoiceId}", true); } catch { }

            try
            {
                await notificationService.CreateNotificationAsync(
                    CreditNoteNotificationFactory.CreateCreatedNotification(creditNote),
                    cancellationToken);
            }
            catch (Exception notificationException)
            {
                logger.LogWarning(
                    notificationException,
                    "Failed to publish credit note notification for {CreditNoteNumber} (from invoice {InvoiceId})",
                    creditNote.CreditNoteNumber,
                    command.InvoiceId);
            }

            return creditNote;
        }
        catch (InvalidOperationException ex)
        {
            // The service's own refusals: an invoice it cannot read, one already fully credited, a
            // line that does not add up. None of them has posted anything.
            releaseIdempotencyRequest &= NothingWasCreated(postIssued: false, ex);
            return Errors.CreditNote.InvalidOperation(ex.Message);
        }
        catch (SapRequestRejectedException rejected)
        {
            // SAP answered, and the answer was no. Nothing exists, so the claim goes back below and
            // the caller can fix the document and re-send under the same key.
            logger.LogWarning(rejected, "SAP refused the credit note for invoice {InvoiceId}", command.InvoiceId);
            return Errors.CreditNote.SapRejected(rejected.SapMessage);
        }
        catch (Exception ex)
        {
            releaseIdempotencyRequest &= NothingWasCreated(postIssued, ex);
            logger.LogError(ex, "Error creating credit note from invoice {InvoiceId}", command.InvoiceId);
            return Errors.CreditNote.CreationFailed(ex.Message);
        }
        finally
        {
            // Released only when nothing can have been created. A claim left standing is doing its
            // job: it is the only record that a post went out under this key, so the retry asks SAP
            // instead of posting again. Releasing it unconditionally — which this did — turned a
            // lost reply into a second credit note, and a second ZIMRA credit receipt.
            if (releaseIdempotencyRequest && idempotencyRequestId.HasValue)
            {
                try
                {
                    await idempotencyRequestStore.ReleaseAsync(idempotencyRequestId.Value, CancellationToken.None);
                }
                catch (Exception releaseException)
                {
                    logger.LogWarning(releaseException, "Failed to release credit note (from invoice) idempotency request {RequestId}", idempotencyRequestId.Value);
                }
            }
        }
    }

    /// <summary>
    /// Whether a failure proves SAP created nothing, and so whether the claim may be given back.
    /// </summary>
    /// <remarks>
    /// The closed list lives in <see cref="SapFailureClassifier.DefinitelyNotCommitted"/>. Everything
    /// else — a timeout, a dropped connection, a reply that could not be read — leaves the credit
    /// note's existence unknown, and unknown has to be treated as "it may exist".
    /// </remarks>
    private static bool NothingWasCreated(bool postIssued, Exception failure) =>
        !postIssued || SapFailureClassifier.DefinitelyNotCommitted(failure);
}
