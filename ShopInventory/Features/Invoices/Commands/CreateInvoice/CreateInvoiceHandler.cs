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

        List<string>? acquiredLockTokens = null;
        long? idempotencyRequestId = null;
        var releaseIdempotencyRequest = false;

        // The claim taken off the shared stock ledger, and whether the post that justifies it went
        // through. Held here so the failure paths can decide whether to give the units back.
        List<StockLedgerLine>? ledgerClaim = null;
        var ledgerCommitted = false;

        try
        {
            // Prefer the U_Van_saleorder business key; fall back to a client-supplied idempotency key
            // (Idempotency-Key header) so plain web invoices without a van-sale-order are also deduped.
            var idempotencyKey = !string.IsNullOrWhiteSpace(request.U_Van_saleorder)
                ? request.U_Van_saleorder
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
                        return Errors.Idempotency.RequestInProgress("invoice creation");
                    case IdempotencyAcquireOutcome.RequestMismatch:
                        return Errors.Idempotency.RequestMismatch("invoice creation");
                    case IdempotencyAcquireOutcome.Acquired:
                        idempotencyRequestId = acquireResult.RequestId;
                        releaseIdempotencyRequest = true;
                        break;
                }
            }

            // Step 1b: Check for duplicate invoice by U_Van_saleorder
            if (!string.IsNullOrWhiteSpace(request.U_Van_saleorder))
            {
                var existingInvoice = await sapClient.GetInvoiceByVanSaleOrderAsync(request.U_Van_saleorder, cancellationToken);
                if (existingInvoice != null)
                {
                    logger.LogWarning(
                        "Duplicate invoice detected. U_Van_saleorder '{VanSaleOrder}' already exists as DocEntry {DocEntry}, DocNum {DocNum}",
                        request.U_Van_saleorder, existingInvoice.DocEntry, existingInvoice.DocNum);

                    var existingResponse = new InvoiceCreatedResponseDto
                    {
                        Message = "Invoice already exists; returning existing document",
                        Invoice = existingInvoice.ToDto(),
                        Fiscalization = null
                    };

                    if (idempotencyRequestId.HasValue)
                    {
                        try
                        {
                            await idempotencyRequestStore.CompleteAsync(idempotencyRequestId.Value, existingResponse, cancellationToken);
                            releaseIdempotencyRequest = false;
                        }
                        catch (Exception completeException)
                        {
                            logger.LogWarning(completeException, "Failed to persist invoice idempotency replay for request {RequestId}", idempotencyRequestId.Value);
                        }
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

            if (idempotencyRequestId.HasValue)
            {
                try
                {
                    await idempotencyRequestStore.CompleteAsync(idempotencyRequestId.Value, response, cancellationToken);
                    releaseIdempotencyRequest = false;
                }
                catch (Exception completeException)
                {
                    logger.LogWarning(completeException, "Failed to persist invoice idempotency completion for request {RequestId}", idempotencyRequestId.Value);
                }
            }

            return response;
        }
        catch (ArgumentException ex)
        {
            // Thrown by the client's own checks before anything is sent, so SAP has not seen it.
            await ReturnLedgerClaimAsync(ledgerClaim, ledgerCommitted, "validation error");
            logger.LogWarning(ex, "Validation error creating invoice");
            try { await auditService.LogAsync(AuditActions.CreateInvoice, "Invoice", null, $"Validation error: {ex.Message}", false, ex.Message); } catch { }
            return Errors.Invoice.ValidationFailed(ex.Message);
        }
        catch (SapPostingPeriodException ex)
        {
            // SAP answered, and its answer was no. The document does not exist.
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
            logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return Errors.Invoice.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.Invoice.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
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
