using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Crates;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.Common.Sales;
using ShopInventory.Common.Validation;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Invoices.Events;
using ShopInventory.Features.Notifications;
using ShopInventory.Mappings;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.Invoices.Commands.CreateInvoice;

public sealed class CreateInvoiceHandler(
    ISAPServiceLayerClient sapClient,
    ILocalPriceCatalogService localPriceCatalogService,
    IBatchInventoryValidationService batchValidation,
    IInventoryLockService lockService,
    IStockLedger stockLedger,
    IInvoiceFiscalizationQueue fiscalizationQueue,
    IAuditService auditService,
    IIdempotencyRequestStore idempotencyRequestStore,
    INotificationService notificationService,
    ApplicationDbContext context,
    IOptions<SAPSettings> settings,
    IOptions<SecuritySettings> securitySettings,
    ILogger<CreateInvoiceHandler> logger
) : IRequestHandler<CreateInvoiceCommand, ErrorOr<InvoiceCreatedResponseDto>>
{
    public async Task<ErrorOr<InvoiceCreatedResponseDto>> Handle(
        CreateInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Invoice.SapDisabled;

        var request = command.Request;
        request.U_Van_saleorder = string.IsNullOrWhiteSpace(request.U_Van_saleorder)
            ? null
            : request.U_Van_saleorder.Trim();

        // U_Van_saleorder is not just a label. Several routes search on it to decide whether SAP
        // already holds a document, and it is this handler's own idempotency key a few lines below.
        // A caller writing a value the system generates for itself would make one of those searches
        // find the wrong invoice and adopt it — the quiet failure, where a sale is marked posted
        // against a document that has nothing to do with it and nothing looks wrong.
        if (request.U_Van_saleorder is { } saleReference)
        {
            if (SaleReferenceNamespace.IsReserved(saleReference))
            {
                return Errors.Invoice.ReservedSaleReference(saleReference);
            }

            // Prefixes cover what the server generates. A till's reference has whatever shape the
            // client chose, so it is caught by looking it up instead — which needs no agreement
            // between the two codebases about formats. ExternalReferenceId is uniquely indexed.
            var belongsToASale = await context.DesktopSales
                .AsNoTracking()
                .AnyAsync(sale => sale.ExternalReferenceId == saleReference, cancellationToken);

            if (belongsToASale)
            {
                return Errors.Invoice.ReservedSaleReference(saleReference);
            }
        }

        // The business key SAP is asked about, for a caller that named none of its own.
        //
        // Every other producer of an invoice writes one: till sales, van sales, consolidation,
        // reservations. This route did not, and ClientRequestId is never sent to the Service Layer —
        // so a web invoice left nothing in SAP to ask about, and a post whose reply was lost could
        // not be found by the retry that followed it. Deriving a reference from the caller's own
        // idempotency key gives this route the same probe every other one has, without asking any
        // client to send something new.
        var callerSuppliedSaleReference = request.U_Van_saleorder;
        if (callerSuppliedSaleReference is null && !string.IsNullOrWhiteSpace(request.ClientRequestId))
        {
            request.U_Van_saleorder = SaleReferenceNamespace.ForClientRequest(request.ClientRequestId);
        }

        List<string>? acquiredLockTokens = null;
        long? idempotencyRequestId = null;
        var releaseIdempotencyRequest = false;
        var claimCompleted = false;

        // The claim taken off the shared stock ledger, and whether the post that justifies it went
        // through. Held here so the failure paths can decide whether to give the units back.
        List<StockLedgerLine>? ledgerClaim = null;
        var ledgerCommitted = false;

        // Whether the request to SAP has left this process. Past that point the invoice may exist
        // whatever the failure looks like from here, so the catches below must not treat an error as
        // proof that nothing was created — see the release decision in the finally.
        var postIssued = false;

        // Whether SAP has already been asked about this key on this pass, so the guard below does
        // not repeat a lookup the in-progress branch has just done.
        var sapAlreadyAsked = false;

        try
        {
            // Prefer the U_Van_saleorder business key; fall back to a client-supplied idempotency key
            // (Idempotency-Key header) so plain web invoices without a van-sale-order are also deduped.
            //
            // The caller's own values, deliberately, and not the reference derived above: that one is
            // a function of ClientRequestId, so keying on it would name the same claims differently
            // and orphan every claim in flight across a deploy.
            var idempotencyKey = !string.IsNullOrWhiteSpace(callerSuppliedSaleReference)
                ? callerSuppliedSaleReference
                : (string.IsNullOrWhiteSpace(request.ClientRequestId) ? null : request.ClientRequestId.Trim());

            if (idempotencyKey is not null)
            {
                var acquireResult = await idempotencyRequestStore.TryAcquireAsync<InvoiceCreatedResponseDto>(
                    "invoices.create",
                    idempotencyKey,
                    request,
                    cancellationToken);

                switch (acquireResult.Outcome)
                {
                    case IdempotencyAcquireOutcome.ReplayAvailable when acquireResult.Response is not null:
                        return acquireResult.Response;
                    case IdempotencyAcquireOutcome.InProgress:
                    {
                        // A claim nobody completed. That used to mean only one thing — a request
                        // running right now — and refusing was the whole answer. It can now also
                        // mean an attempt that issued its post and never learned what became of it,
                        // because such a claim is deliberately kept rather than released. Refusing
                        // outright would leave the caller with no way to reach an invoice that does
                        // exist, so ask SAP before answering.
                        //
                        // A lookup that cannot be answered throws, and that is the point: treating
                        // "I could not ask" as "it is not there" is how an invoice gets posted twice.
                        var abandonedPost = await sapClient.GetInvoiceByVanSaleOrderAsync(
                            request.U_Van_saleorder!, cancellationToken);
                        sapAlreadyAsked = true;

                        if (abandonedPost is not null)
                        {
                            logger.LogWarning(
                                "An unfinished invoice claim for '{SaleReference}' is held in SAP as DocEntry {DocEntry}; returning it.",
                                request.U_Van_saleorder, abandonedPost.DocEntry);

                            var abandonedResponse = ExistingInvoiceResponse(abandonedPost);
                            await TryCompleteClaimAsync(acquireResult.RequestId, abandonedResponse, cancellationToken);
                            return abandonedResponse;
                        }

                        // SAP does not show it. Inside the grace window that "no" is not an answer:
                        // a committed invoice that is not yet visible looks exactly like this, and a
                        // second invoice is a second ZIMRA receipt that cannot be withdrawn. So the
                        // caller waits. Past the window, with SAP still showing nothing, the post
                        // never landed and the claim is taken over so this attempt can make it good
                        // — an unfinished claim must not park an invoice forever.
                        var issuedBefore = DateTime.UtcNow.AddMinutes(
                            -Math.Max(0, securitySettings.Value.IdempotencyUnresolvedPostGraceMinutes));

                        if (acquireResult.RequestId is not { } unfinishedRequestId
                            || !await idempotencyRequestStore.TryTakeOverAsync(
                                unfinishedRequestId, issuedBefore, cancellationToken))
                        {
                            logger.LogWarning(
                                "Invoice creation for '{SaleReference}' was not sent again: an earlier claim is unfinished and SAP does not show the document.",
                                request.U_Van_saleorder);

                            return Errors.Idempotency.PostOutcomeUnknown(
                                "invoice creation", request.U_Van_saleorder!);
                        }

                        logger.LogWarning(
                            "Took over an unfinished invoice claim for '{SaleReference}': SAP does not hold it and the grace window has passed.",
                            request.U_Van_saleorder);

                        idempotencyRequestId = unfinishedRequestId;
                        releaseIdempotencyRequest = true;
                        break;
                    }
                    case IdempotencyAcquireOutcome.RequestMismatch:
                        return Errors.Idempotency.RequestMismatch("invoice creation");
                    case IdempotencyAcquireOutcome.Acquired:
                        idempotencyRequestId = acquireResult.RequestId;
                        releaseIdempotencyRequest = true;
                        break;
                }
            }

            // Step 1b: Check for duplicate invoice by U_Van_saleorder
            if (!sapAlreadyAsked && !string.IsNullOrWhiteSpace(request.U_Van_saleorder))
            {
                var existingInvoice = await sapClient.GetInvoiceByVanSaleOrderAsync(request.U_Van_saleorder, cancellationToken);
                if (existingInvoice != null)
                {
                    logger.LogWarning(
                        "Duplicate invoice detected. U_Van_saleorder '{VanSaleOrder}' already exists as DocEntry {DocEntry}, DocNum {DocNum}",
                        request.U_Van_saleorder, existingInvoice.DocEntry, existingInvoice.DocNum);

                    var existingResponse = ExistingInvoiceResponse(existingInvoice);

                    if (await TryCompleteClaimAsync(idempotencyRequestId, existingResponse, cancellationToken))
                    {
                        releaseIdempotencyRequest = false;
                        claimCompleted = true;
                    }

                    return existingResponse;
                }
            }

            var missingPriceItems = await PopulateStoredPricesAsync(request, cancellationToken);
            if (missingPriceItems.Count > 0)
            {
                return Errors.Invoice.ValidationFailed(
                    $"No SAP price is set for item(s): {string.Join(", ", missingPriceItems)}. Please contact the admin.");
            }

            // Step 2: Validate basic quantities
            var quantityErrors = await ValidateLinesAsync(request, cancellationToken);
            if (quantityErrors.Count > 0)
                return Errors.Invoice.ValidationFailed($"Quantity validation failed: {string.Join("; ", quantityErrors)}");

            // Step 3: Validate warehouse codes
            var warehouseErrors = ValidateWarehouseCodes(request);
            if (warehouseErrors.Count > 0)
                return Errors.Invoice.ValidationFailed($"Warehouse validation failed: {string.Join("; ", warehouseErrors)}");

            logger.LogInformation(
                "Validating batch stock for invoice with {LineCount} lines. AutoAllocate: {AutoAllocate}, Strategy: {Strategy}",
                request.Lines?.Count ?? 0, command.AutoAllocateBatches, command.AllocationStrategy);

            // Step 4: Batch-level validation with FIFO/FEFO auto-allocation
            var batchValidationResult = await batchValidation.ValidateAndAllocateBatchesAsync(
                request, command.AutoAllocateBatches, command.AllocationStrategy, cancellationToken);

            if (!batchValidationResult.IsValid)
            {
                logger.LogWarning("Batch validation failed for invoice creation. {ErrorCount} errors. Strategy: {Strategy}",
                    batchValidationResult.ValidationErrors.Count, command.AllocationStrategy);

                // An unread stock position is not a failed validation, and saying so matters: the
                // caller's document is fine and retrying is the correct response, where "would cause
                // negative quantities" tells them to go and cut lines out of it.
                var unreadable = batchValidationResult.ValidationErrors
                    .Where(e => e.ErrorCode == BatchValidationErrorCode.StockUnknown)
                    .ToList();
                if (unreadable.Count > 0)
                {
                    return Errors.Invoice.StockUnknown(string.Join("; ", unreadable.Select(e => e.Message)));
                }

                return Errors.Invoice.BatchValidationFailed(
                    $"Batch validation failed - would cause negative quantities: {string.Join("; ", batchValidationResult.ValidationErrors.Select(e => e.Message))}");
            }

            // Step 5: Apply auto-allocated batches
            if (batchValidationResult.BatchesAutoAllocated && batchValidationResult.AllocatedLines.Count > 0)
            {
                ApplyAllocatedBatchesToRequest(request, batchValidationResult.AllocatedLines);
                logger.LogInformation("Applied auto-allocated batches to {LineCount} lines using {Strategy} strategy",
                    batchValidationResult.AllocatedLines.Count, command.AllocationStrategy);
            }

            // Step 6: Pre-post validation with locks
            var prePostResult = await batchValidation.PrePostValidationAsync(
                request, batchValidationResult.AllocatedLines, cancellationToken);

            if (!prePostResult.IsValid)
            {
                var lockErrors = prePostResult.Errors
                    .Where(e => e.ErrorCode == BatchValidationErrorCode.LockAcquisitionFailed)
                    .ToList();

                if (lockErrors.Count > 0)
                {
                    logger.LogWarning("Lock acquisition failed for invoice creation - concurrent access detected");
                    return Errors.Invoice.LockConflict;
                }

                logger.LogWarning("Pre-post validation failed - stock may have changed. {ErrorCount} errors", prePostResult.Errors.Count);

                var unreadableAtPrePost = prePostResult.Errors
                    .Where(e => e.ErrorCode == BatchValidationErrorCode.StockUnknown)
                    .ToList();
                if (unreadableAtPrePost.Count > 0)
                {
                    return Errors.Invoice.StockUnknown(string.Join("; ", unreadableAtPrePost.Select(e => e.Message)));
                }

                return Errors.Invoice.StockValidationFailed(
                    $"Pre-post validation failed - stock levels changed during processing: {string.Join("; ", prePostResult.Errors.Select(e => e.Message))}");
            }

            if (!string.IsNullOrEmpty(prePostResult.LockToken))
            {
                acquiredLockTokens = prePostResult.LockTokens.Count > 0
                    ? prePostResult.LockTokens
                    : new List<string> { prePostResult.LockToken };
            }

            // Step 6b: Take the units off the shared ledger, before the post rather than after.
            //
            // The SAP read above cannot see a till sale that has been captured and has not yet
            // posted — up to a minute, longer if SAP is refusing it — so on its own it will happily
            // sell the same units the shop floor has already handed over. The ledger is what knows
            // about them. Taking the claim first means a sale that loses the race is refused rather
            // than posted and then discovered.
            //
            // Warehouses with no snapshot are passed over, not refused: most warehouses have no till
            // selling from them, so there is no second consumer to collide with and SAP is authority
            // enough.
            ledgerClaim = request.Lines?
                .Select(line => new StockLedgerLine(line.ItemCode ?? string.Empty, line.WarehouseCode ?? string.Empty, line.Quantity))
                .ToList() ?? [];

            var ledgerReference = request.U_Van_saleorder ?? request.NumAtCard ?? "invoice";
            var ledgerOutcome = await stockLedger.TryCommitAsync(ledgerClaim, ledgerReference, cancellationToken);

            if (!ledgerOutcome.Committed)
            {
                ledgerClaim = null;
                logger.LogWarning(
                    "Invoice refused by the shared stock ledger: {Shortfalls}",
                    string.Join("; ", ledgerOutcome.Shortfalls));
                return Errors.Invoice.LedgerShortfall(string.Join("; ", ledgerOutcome.Shortfalls));
            }

            // Step 7: POST to SAP
            postIssued = true;
            var invoice = await sapClient.CreateInvoiceAsync(request, cancellationToken);
            ledgerCommitted = true;

            logger.LogInformation(
                "Invoice created successfully in SAP. DocEntry: {DocEntry}, DocNum: {DocNum}, Customer: {CardCode}, BatchesAllocated: {BatchCount}, Strategy: {Strategy}",
                invoice.DocEntry, invoice.DocNum, invoice.CardCode,
                batchValidationResult.AllocatedLines.Sum(l => l.Batches.Count), command.AllocationStrategy);

            await RegisterCrateTransactionAsync(invoice, request, command.UserId, cancellationToken);

            try { await auditService.LogAsync(AuditActions.CreateInvoice, "Invoice", invoice.DocEntry.ToString(), $"Invoice #{invoice.DocNum} created for {invoice.CardCode}", true); } catch { }

            var invoiceDto = invoice.ToDto();
            var fiscalizationResult = QueueFiscalization(invoiceDto, command.UserId, command.Username);

            // Targeted at whoever raised it, not broadcast. Invoices are the highest-volume document
            // in the system — one per delivery — so a broadcast here would turn the bell over every
            // few minutes and bury every other module, which is the exact failure the POD notification
            // used to cause. Each user seeing their own keeps the volume proportionate.
            if (!string.IsNullOrWhiteSpace(command.Username))
            {
                try
                {
                    await notificationService.CreateNotificationAsync(
                        WorkflowNotificationFactory.CreateInvoiceCreatedNotification(
                            command.UserId,
                            command.Username,
                            invoiceDto,
                            reservationId: null,
                            "/invoices",
                            fiscalizationResult),
                        cancellationToken);
                }
                catch (Exception notificationException)
                {
                    logger.LogWarning(
                        notificationException,
                        "Failed to publish invoice notification for DocEntry {DocEntry}",
                        invoice.DocEntry);
                }
            }

            var response = new InvoiceCreatedResponseDto
            {
                Message = fiscalizationResult.Queued
                    ? "Invoice created successfully; fiscalization is running in the background"
                    : "Invoice created successfully (fiscalization pending)",
                Invoice = invoiceDto,
                Fiscalization = fiscalizationResult
            };

            if (await TryCompleteClaimAsync(idempotencyRequestId, response, cancellationToken))
            {
                releaseIdempotencyRequest = false;
                claimCompleted = true;
            }

            return response;
        }
        catch (ArgumentException ex)
        {
            // Thrown by the client's own checks before anything is sent, so SAP has not seen it.
            releaseIdempotencyRequest &= NothingWasCreated(postIssued, ex);
            await ReturnLedgerClaimAsync(ledgerClaim, ledgerCommitted, "validation error");
            logger.LogWarning(ex, "Validation error creating invoice");
            try { await auditService.LogAsync(AuditActions.CreateInvoice, "Invoice", null, $"Validation error: {ex.Message}", false, ex.Message); } catch { }
            return Errors.Invoice.ValidationFailed(ex.Message);
        }
        catch (SapPostingPeriodException ex)
        {
            // SAP answered, and its answer was no. The document does not exist.
            releaseIdempotencyRequest &= NothingWasCreated(postIssued, ex);
            await ReturnLedgerClaimAsync(ledgerClaim, ledgerCommitted, "posting period rejected");
            logger.LogWarning(
                "SAP rejected invoice document dates starting from DocDate {DocDate}: {Message}",
                ex.DocDate,
                ex.Message);
            try { await auditService.LogAsync(AuditActions.CreateInvoice, "Invoice", null, $"Posting period error for invoice dates; DocDate {ex.DocDate}", false, ex.Message); } catch { }
            return Errors.Invoice.PostingPeriodInvalid(ex.Message);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            releaseIdempotencyRequest &= NothingWasCreated(postIssued, ex);
            logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return Errors.Invoice.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            releaseIdempotencyRequest &= NothingWasCreated(postIssued, ex);
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.Invoice.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            releaseIdempotencyRequest &= NothingWasCreated(postIssued, ex);
            logger.LogError(ex, "Error creating invoice");
            return Errors.Invoice.CreationFailed(ex.Message);
        }
        finally
        {
            if (acquiredLockTokens != null && acquiredLockTokens.Count > 0)
            {
                try
                {
                    await lockService.ReleaseMultipleLocksAsync(acquiredLockTokens);
                    logger.LogDebug("Released {LockCount} inventory locks after invoice processing", acquiredLockTokens.Count);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to release inventory locks - they will expire automatically");
                }
            }

            // Released only when nothing can have been created. The catches above decide that, and
            // a claim they leave standing is doing its job: it is the only record that a post went
            // out under this key, so the retry asks SAP instead of posting again. Releasing it
            // unconditionally — which this did — turned a lost reply into a second invoice, because
            // the retry re-acquired a clean claim and posted.
            if (releaseIdempotencyRequest && idempotencyRequestId.HasValue)
            {
                try
                {
                    await idempotencyRequestStore.ReleaseAsync(idempotencyRequestId.Value, CancellationToken.None);
                }
                catch (Exception releaseException)
                {
                    logger.LogWarning(releaseException, "Failed to release invoice idempotency request {RequestId}", idempotencyRequestId.Value);
                }
            }
            else if (idempotencyRequestId.HasValue && !claimCompleted)
            {
                logger.LogWarning(
                    "Keeping the invoice claim for '{SaleReference}': a post was issued and its outcome is unknown, so a retry must ask SAP rather than post again.",
                    request.U_Van_saleorder);
            }
        }
    }

    /// <summary>
    /// Whether a failure proves SAP created nothing, and so whether the claim may be given back.
    /// </summary>
    /// <remarks>
    /// The list of failures that prove it is short, closed, and lives in
    /// <see cref="SapFailureClassifier.DefinitelyNotCommitted"/> — the same judgement the background
    /// posting services make before they send a sale again. Everything else, a timeout and a dropped
    /// connection above all, leaves the document's existence unknown, and unknown has to be treated
    /// as "it may exist": the cost of being wrong is a second invoice and a second ZIMRA receipt
    /// against one sale, which cannot be withdrawn.
    ///
    /// <para>A failure raised before the post went out proves it just as well, whatever its type.</para>
    /// </remarks>
    private static bool NothingWasCreated(bool postIssued, Exception failure) =>
        !postIssued || SapFailureClassifier.DefinitelyNotCommitted(failure);

    private static InvoiceCreatedResponseDto ExistingInvoiceResponse(Invoice existing) =>
        new()
        {
            Message = "Invoice already exists; returning existing document",
            Invoice = existing.ToDto(),
            Fiscalization = null
        };

    /// <summary>
    /// Stores the response against the claim so a later retry is answered with the real document.
    /// </summary>
    /// <returns>True when the claim now holds the response, so it must not be released.</returns>
    private async Task<bool> TryCompleteClaimAsync(
        long? idempotencyRequestId,
        InvoiceCreatedResponseDto response,
        CancellationToken cancellationToken)
    {
        if (!idempotencyRequestId.HasValue)
        {
            return false;
        }

        try
        {
            await idempotencyRequestStore.CompleteAsync(idempotencyRequestId.Value, response, cancellationToken);
            return true;
        }
        catch (Exception completeException)
        {
            logger.LogWarning(
                completeException,
                "Failed to persist invoice idempotency completion for request {RequestId}",
                idempotencyRequestId.Value);
            return false;
        }
    }

    private FiscalizationResult QueueFiscalization(InvoiceDto invoice, Guid? userId, string? username)
    {
        var queued = fiscalizationQueue.TryQueue(new InvoiceFiscalizationWorkItem(
            invoice,
            new CustomerFiscalDetails { CustomerName = invoice.CardName },
            userId,
            username,
            "/invoices"));

        if (queued)
        {
            logger.LogInformation(
                "Queued fiscalization for invoice {DocNum} (DocEntry: {DocEntry})",
                invoice.DocNum,
                invoice.DocEntry);

            return new FiscalizationResult
            {
                Success = true,
                Queued = true,
                Message = "Fiscalization queued",
                InvoiceNumber = invoice.DocNum.ToString()
            };
        }

        logger.LogError(
            "Failed to queue fiscalization for invoice {DocNum} (DocEntry: {DocEntry})",
            invoice.DocNum,
            invoice.DocEntry);

        return new FiscalizationResult
        {
            Success = false,
            Message = "Fiscalization could not be queued",
            ErrorCode = "FISCALIZATION_QUEUE_UNAVAILABLE",
            InvoiceNumber = invoice.DocNum.ToString()
        };
    }

    private async Task RegisterCrateTransactionAsync(
        Invoice invoice,
        CreateInvoiceRequest request,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        var crateQuantity = request.CrateQuantity.GetValueOrDefault();
        if (crateQuantity <= 0)
        {
            return;
        }

        try
        {
            var transaction = await context.CrateTransactions
                .FirstOrDefaultAsync(t => t.InvoiceDocEntry == invoice.DocEntry, cancellationToken);

            if (transaction is null)
            {
                transaction = new CrateTransactionEntity
                {
                    TransactionType = CrateTrackingConstants.TransactionTypeInvoice,
                    InvoiceDocEntry = invoice.DocEntry,
                    InvoiceDocNum = invoice.DocNum,
                    ShopCardCode = invoice.CardCode ?? request.CardCode ?? string.Empty,
                    ShopName = invoice.CardName,
                    ExpectedQuantity = crateQuantity,
                    EffectiveDate = ResolveCrateEffectiveDate(invoice.DocDate),
                    Notes = string.IsNullOrWhiteSpace(request.Comments) ? null : request.Comments.Trim(),
                    CreatedAt = DateTime.UtcNow,
                    CreatedByUserId = userId
                };

                context.CrateTransactions.Add(transaction);
            }
            else
            {
                transaction.InvoiceDocNum = invoice.DocNum;
                transaction.ShopCardCode = invoice.CardCode ?? transaction.ShopCardCode;
                transaction.ShopName = invoice.CardName;
                transaction.ExpectedQuantity = crateQuantity;
                transaction.EffectiveDate = ResolveCrateEffectiveDate(invoice.DocDate, transaction.EffectiveDate);
                transaction.Notes = string.IsNullOrWhiteSpace(request.Comments) ? transaction.Notes : request.Comments.Trim();
                transaction.UpdatedAt = DateTime.UtcNow;
                transaction.CreatedByUserId ??= userId;
            }

            await context.SaveChangesAsync(cancellationToken);

            try
            {
                await auditService.LogAsync(
                    AuditActions.RegisterInvoiceCrates,
                    "CrateTransaction",
                    transaction.Id.ToString(),
                    $"Registered {crateQuantity:N2} expected crates for invoice #{invoice.DocNum}",
                    true);
            }
            catch
            {
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to register crate tracking record for invoice {DocEntry}", invoice.DocEntry);
        }
    }

    private static DateTime ResolveCrateEffectiveDate(string? docDate, DateTime? fallback = null)
    {
        if (DateTime.TryParse(docDate, out var parsedDate))
        {
            return DateTime.SpecifyKind(parsedDate.Date, DateTimeKind.Utc);
        }

        return fallback ?? DateTime.UtcNow.Date;
    }

    /// <summary>
    /// The line-level checks that run before anything is allocated or posted.
    /// </summary>
    /// <remarks>
    /// This used to be a hand-rolled loop, and the invoice was the only sales document that had
    /// one. Every other — quotations, sales orders, transfers, purchase orders — goes through
    /// <see cref="UomQuantityValidation.ValidateAndNormalizeLineQuantitiesAsync"/>, so the rule that
    /// only KG items may carry a fractional quantity was enforced everywhere except on the document
    /// that actually moves the stock. A weighed quantity on a whole-unit item reached SAP and was
    /// rounded there, and the invoice and the shelf disagreed by the remainder.
    ///
    /// <para>
    /// The batch and serial selection checks come from the same shared helper the SAP client uses,
    /// so a caller now learns about a selection that does not add up here — naming the line — rather
    /// than as an <c>ArgumentException</c> thrown from inside the posting client.
    /// </para>
    ///
    /// <para>
    /// The price check stays local. It is not a quantity rule, and its message is about
    /// configuration rather than about what the caller sent.
    /// </para>
    /// </remarks>
    private async Task<List<string>> ValidateLinesAsync(
        CreateInvoiceRequest request,
        CancellationToken cancellationToken)
    {
        var errors = await UomQuantityValidation.ValidateAndNormalizeLineQuantitiesAsync(
            context,
            request.Lines,
            line => line.ItemCode,
            line => line.Quantity,
            line => line.UoMCode,
            (line, uomCode) => line.UoMCode = uomCode,
            cancellationToken);

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return errors;
        }

        for (var i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];

            if (!line.UnitPrice.HasValue || line.UnitPrice.Value <= 0)
            {
                errors.Add($"Line {i + 1} (Item: {line.ItemCode ?? "unknown"}): No SAP price is set for this item. Please contact the admin.");
            }

            errors.AddRange(UomQuantityValidation.DescribeLineSelectionProblems(
                i,
                line.ItemCode,
                line.Quantity,
                line.BatchNumbers?.Select(batch => (batch.BatchNumber, batch.Quantity)),
                line.SerialNumbers?.Select(serial => serial.InternalSerialNumber)));
        }

        return errors;
    }

    private async Task<List<string>> PopulateStoredPricesAsync(CreateInvoiceRequest request, CancellationToken cancellationToken)
    {
        var missingPriceItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (request.Lines == null || request.Lines.Count == 0 || string.IsNullOrWhiteSpace(request.CardCode))
        {
            return [];
        }

        var itemCodes = request.Lines
            .Select(line => line.ItemCode?.Trim())
            .Where(itemCode => !string.IsNullOrWhiteSpace(itemCode))
            .Select(itemCode => itemCode!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (itemCodes.Count == 0)
        {
            return [];
        }

        logger.LogInformation(
            "Resolving invoice prices from the local price catalog for customer {CardCode} across {ItemCount} item(s)",
            request.CardCode,
            itemCodes.Count);

        var pricing = await localPriceCatalogService.GetBusinessPartnerPricingAsync(request.CardCode, itemCodes, cancellationToken);
        var priceMap = pricing?.Prices.Prices?
            .Where(price => !string.IsNullOrWhiteSpace(price.ItemCode))
            .ToDictionary(price => price.ItemCode!, price => price.Price, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in request.Lines)
        {
            var itemCode = line.ItemCode?.Trim();
            if (string.IsNullOrWhiteSpace(itemCode))
            {
                continue;
            }

            if (priceMap.TryGetValue(itemCode, out var resolvedPrice) && resolvedPrice > 0)
            {
                line.UnitPrice = resolvedPrice;
                continue;
            }

            line.UnitPrice = 0m;
            missingPriceItems.Add(itemCode);

            logger.LogWarning(
                "No locally synced price configured for invoice item {ItemCode} and customer {CardCode}",
                itemCode,
                request.CardCode);
        }

        return missingPriceItems.OrderBy(itemCode => itemCode).ToList();
    }

    /// <summary>
    /// Gives a ledger claim back, when — and only when — SAP definitely did not create the document.
    /// </summary>
    /// <remarks>
    /// The claim is taken immediately before the post, so every failure after it is a question about
    /// what SAP did with the request. Two answers are unambiguous: the client refused it before
    /// sending, and SAP answered with a rejection. Everything else — a dropped connection, a
    /// timeout — may have committed, and giving the units back there would let them be sold twice.
    ///
    /// <para>
    /// So the bias is deliberate and one-directional: a claim held over a document that does not
    /// exist makes the ledger short, which refuses sales that could have happened, and the morning
    /// fetch restates it. A claim released over a document that does exist oversells, which is the
    /// failure this whole ledger was built to stop.
    /// </para>
    /// </remarks>
    private async Task ReturnLedgerClaimAsync(
        List<StockLedgerLine>? claim,
        bool alreadyPosted,
        string reason)
    {
        if (claim is null || claim.Count == 0 || alreadyPosted)
        {
            return;
        }

        try
        {
            await stockLedger.ReleaseAsync(claim, $"invoice not posted ({reason})", CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Worth a warning and nothing more. The ledger being short is the safe direction, and
            // the morning fetch clears it.
            logger.LogWarning(ex, "Could not return the stock ledger claim after {Reason}", reason);
        }
    }

    private static List<string> ValidateWarehouseCodes(CreateInvoiceRequest request)
    {
        var errors = new List<string>();
        if (request.Lines == null) return errors;
        for (int i = 0; i < request.Lines.Count; i++)
            if (string.IsNullOrWhiteSpace(request.Lines[i].WarehouseCode))
                errors.Add($"Line {i + 1} (Item: {request.Lines[i].ItemCode ?? "unknown"}): Warehouse code is required for each invoice line.");
        return errors;
    }

    private void ApplyAllocatedBatchesToRequest(CreateInvoiceRequest request, List<AllocatedBatchLine> allocatedLines)
    {
        if (request.Lines == null) return;
        foreach (var allocatedLine in allocatedLines)
        {
            var lineIndex = allocatedLine.LineNumber - 1;
            if (lineIndex < 0 || lineIndex >= request.Lines.Count) continue;
            var requestLine = request.Lines[lineIndex];
            if (requestLine.BatchNumbers == null || requestLine.BatchNumbers.Count == 0)
            {
                if (allocatedLine.Batches.Count > 0)
                {
                    requestLine.BatchNumbers = allocatedLine.Batches
                        .Select(b => new BatchNumberRequest
                        {
                            BatchNumber = b.BatchNumber,
                            Quantity = b.QuantityAllocated,
                            ExpiryDate = b.ExpiryDate
                        }).ToList();
                    logger.LogDebug("Applied {BatchCount} batches to line {LineNumber} for item {ItemCode}",
                        allocatedLine.Batches.Count, allocatedLine.LineNumber, allocatedLine.ItemCode);
                }
            }

            if (requestLine.SerialNumbers == null || requestLine.SerialNumbers.Count == 0)
            {
                if (allocatedLine.Serials.Count > 0)
                {
                    requestLine.SerialNumbers = allocatedLine.Serials
                        .Select(s => new SerialNumberRequest
                        {
                            InternalSerialNumber = s.InternalSerialNumber,
                            SystemSerialNumber = s.SystemSerialNumber
                        }).ToList();
                    logger.LogDebug("Applied {SerialCount} serial numbers to line {LineNumber} for item {ItemCode}",
                        allocatedLine.Serials.Count, allocatedLine.LineNumber, allocatedLine.ItemCode);
                }
            }
        }
    }
}
