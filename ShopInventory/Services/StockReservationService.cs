using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Extensions;
using ShopInventory.Common.Validation;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Invoices.Events;
using ShopInventory.Features.Notifications;
using ShopInventory.Mappings;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using System.Text.Json;

namespace ShopInventory.Services;

/// <summary>
/// Service for managing stock reservations from external systems (desktop app, POS, etc.)
/// Reservations hold stock for committed transactions that haven't been posted to SAP yet.
/// This prevents negative quantities by accounting for pending sales.
/// </summary>
public interface IStockReservationService
{
    /// <summary>
    /// Creates a new stock reservation, validating and allocating stock.
    /// </summary>
    Task<StockReservationResponseDto> CreateReservationAsync(
        CreateStockReservationRequest request,
        string? createdBy = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms a reservation by posting it to SAP as an invoice.
    /// </summary>
    Task<ConfirmReservationResponseDto> ConfirmReservationAsync(
        ConfirmReservationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a reservation and releases the reserved stock.
    /// </summary>
    Task<StockReservationResponseDto> CancelReservationAsync(
        CancelReservationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renews/extends a reservation before it expires.
    /// </summary>
    Task<StockReservationResponseDto> RenewReservationAsync(
        RenewReservationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a reservation by its ID.
    /// </summary>
    Task<StockReservationDto?> GetReservationAsync(
        string reservationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a reservation by its external reference ID.
    /// </summary>
    Task<StockReservationDto?> GetReservationByExternalReferenceAsync(
        string externalReferenceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists reservations with filtering and pagination.
    /// </summary>
    Task<ReservationListResponseDto> ListReservationsAsync(
        ReservationQueryParams query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the total reserved quantity for an item in a warehouse.
    /// Only includes active (pending) reservations.
    /// </summary>
    Task<decimal> GetReservedQuantityAsync(
        string itemCode,
        string warehouseCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the total reserved quantity for a specific batch.
    /// Only includes active (pending) reservations.
    /// </summary>
    Task<decimal> GetReservedBatchQuantityAsync(
        string itemCode,
        string warehouseCode,
        string batchNumber,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, decimal>> GetReservedBatchQuantitiesAsync(
        string itemCode,
        string warehouseCode,
        IEnumerable<string> batchNumbers,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a summary of reserved stock for an item/warehouse.
    /// </summary>
    Task<ReservedStockSummaryDto> GetReservedStockSummaryAsync(
        string itemCode,
        string warehouseCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Expires all reservations that have passed their expiry time.
    /// Called by a background service.
    /// </summary>
    Task<int> ExpireReservationsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates if stock is available considering existing reservations.
    /// </summary>
    Task<(bool IsValid, List<StockReservationErrorDto> Errors)> ValidateStockAvailabilityAsync(
        List<CreateStockReservationLineRequest> lines,
        string? excludeReservationId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementation of stock reservation service
/// </summary>
public class StockReservationService : IStockReservationService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly IBatchInventoryValidationService _batchValidation;
    private readonly IInventoryLockService _lockService;
    private readonly IInvoiceFiscalizationQueue _fiscalizationQueue;
    private readonly INotificationService _notificationService;
    private readonly ILogger<StockReservationService> _logger;

    private const int MaxRenewals = 10;
    private const int MaxReservationDurationHours = 24;

    public StockReservationService(
        ApplicationDbContext dbContext,
        ISAPServiceLayerClient sapClient,
        IBatchInventoryValidationService batchValidation,
        IInventoryLockService lockService,
        IInvoiceFiscalizationQueue fiscalizationQueue,
        INotificationService notificationService,
        ILogger<StockReservationService> logger)
    {
        _dbContext = dbContext;
        _sapClient = sapClient;
        _batchValidation = batchValidation;
        _lockService = lockService;
        _fiscalizationQueue = fiscalizationQueue;
        _notificationService = notificationService;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<StockReservationResponseDto> CreateReservationAsync(
        CreateStockReservationRequest request,
        string? createdBy = null,
        CancellationToken cancellationToken = default)
    {
        var requestValidationErrors = RecursiveDataAnnotationsValidator.Validate(request);
        if (requestValidationErrors.Count > 0)
        {
            return new StockReservationResponseDto
            {
                Success = false,
                Message = $"Reservation request validation failed: {string.Join("; ", requestValidationErrors)}",
                Errors = requestValidationErrors.Select(message => new StockReservationErrorDto
                {
                    ErrorCode = "ValidationError",
                    Message = message,
                    SuggestedAction = "Correct the document quantities and retry"
                }).ToList()
            };
        }

        _logger.LogInformation(
            "Creating stock reservation for {ExternalRef} from {Source} with {LineCount} lines",
            request.ExternalReferenceId, request.SourceSystem, request.Lines.Count);

        // Check for duplicate external reference
        var existingReservation = await _dbContext.StockReservations
            .FirstOrDefaultAsync(r => r.ExternalReferenceId == request.ExternalReferenceId, cancellationToken);

        if (existingReservation != null)
        {
            _logger.LogWarning("Duplicate reservation attempt for external reference {ExternalRef}", request.ExternalReferenceId);

            if (existingReservation.Status == ReservationStatus.Pending)
            {
                // Return the existing active reservation
                var existingDto = await GetReservationAsync(existingReservation.ReservationId, cancellationToken);
                return new StockReservationResponseDto
                {
                    Success = true,
                    Message = "Existing active reservation found for this reference",
                    Reservation = existingDto,
                    Warnings = new List<string> { "Using existing reservation - no new reservation created" }
                };
            }

            return new StockReservationResponseDto
            {
                Success = false,
                Message = "A reservation with this external reference already exists",
                Errors = new List<StockReservationErrorDto>
                {
                    new()
                    {
                        ErrorCode = ReservationErrorCode.DuplicateReference,
                        Message = $"External reference '{request.ExternalReferenceId}' is already used by reservation {existingReservation.ReservationId} (Status: {existingReservation.Status})"
                    }
                }
            };
        }

        // Validate stock availability (considering existing reservations)
        var (isValid, validationErrors) = await ValidateStockAvailabilityAsync(request.Lines, null, cancellationToken);

        if (!isValid)
        {
            _logger.LogWarning(
                "Stock validation failed for reservation {ExternalRef}. {ErrorCount} errors",
                request.ExternalReferenceId, validationErrors.Count);

            return new StockReservationResponseDto
            {
                Success = false,
                Message = "Insufficient stock available for reservation",
                Errors = validationErrors
            };
        }

        // Create the reservation entity
        var reservation = new StockReservationEntity
        {
            ReservationId = Guid.NewGuid().ToString(),
            ExternalReferenceId = request.ExternalReferenceId,
            SourceSystem = request.SourceSystem,
            DocumentType = request.DocumentType,
            CardCode = request.CardCode,
            CardName = request.CardName,
            RouteCustomerId = request.RouteCustomerId,
            RouteCustomerCode = request.RouteCustomerCode,
            RouteCustomerName = request.RouteCustomerName,
            Currency = request.Currency,
            PaymentMethod = request.PaymentMethod,
            Status = ReservationStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(Math.Min(request.ReservationDurationMinutes, MaxReservationDurationHours * 60)),
            CreatedBy = createdBy,
            Metadata = request.Metadata != null ? JsonSerializer.Serialize(request.Metadata) : null
        };

        decimal totalValue = 0;
        var explicitBatchQuantitiesInRequest = BuildExplicitBatchQuantityLookup(request.Lines);
        var autoBatchQuantitiesInRequest = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        // Process each line and allocate batches
        foreach (var lineRequest in request.Lines)
        {
            var line = new StockReservationLineEntity
            {
                LineNum = lineRequest.LineNum,
                ItemCode = lineRequest.ItemCode,
                ItemDescription = lineRequest.ItemDescription,
                OriginalQuantity = lineRequest.Quantity,
                UoMCode = lineRequest.UoMCode,
                WarehouseCode = lineRequest.WarehouseCode,
                UnitPrice = lineRequest.UnitPrice,
                TaxCode = lineRequest.TaxCode,
                DiscountPercent = lineRequest.DiscountPercent,
                CostCentreCode = lineRequest.CostCentreCode
            };

            // Convert quantity to inventory UoM if needed
            decimal inventoryQuantity = lineRequest.Quantity;
            if (!string.IsNullOrEmpty(lineRequest.UoMCode))
            {
                var uomInfo = await _batchValidation.GetUoMConversionAsync(
                    lineRequest.ItemCode, lineRequest.UoMCode, null, cancellationToken);
                if (uomInfo != null && uomInfo.ConversionFactor > 0)
                {
                    inventoryQuantity = lineRequest.Quantity * uomInfo.ConversionFactor;
                }
            }
            line.ReservedQuantity = inventoryQuantity;

            // Calculate line total
            decimal lineTotal = lineRequest.UnitPrice * lineRequest.Quantity;
            if (lineRequest.DiscountPercent > 0)
            {
                lineTotal = lineTotal * (1 - lineRequest.DiscountPercent / 100);
            }
            line.LineTotal = lineTotal;
            totalValue += lineTotal;

            // Allocate batches for batch-managed items
            var isBatchManaged = await _batchValidation.IsBatchManagedItemAsync(lineRequest.ItemCode, cancellationToken);

            if (isBatchManaged)
            {
                if (lineRequest.BatchNumbers != null && lineRequest.BatchNumbers.Count > 0)
                {
                    // Use explicit batch allocations
                    foreach (var batchReq in lineRequest.BatchNumbers)
                    {
                        line.BatchAllocations.Add(new StockReservationBatchEntity
                        {
                            ItemCode = lineRequest.ItemCode,
                            BatchNumber = batchReq.BatchNumber,
                            WarehouseCode = lineRequest.WarehouseCode,
                            ReservedQuantity = batchReq.Quantity
                        });
                    }
                }
                else if (lineRequest.AutoAllocateBatches)
                {
                    // Auto-allocate using FEFO
                    var availableBatches = await _batchValidation.GetAvailableBatchesAsync(
                        lineRequest.ItemCode, lineRequest.WarehouseCode, BatchAllocationStrategy.FEFO, cancellationToken);
                    var reservedByBatch = await GetReservedBatchQuantitiesAsync(
                        lineRequest.ItemCode,
                        lineRequest.WarehouseCode,
                        availableBatches
                            .Select(batch => batch.BatchNumber)
                            .Where(batchNumber => !string.IsNullOrWhiteSpace(batchNumber))
                            .Select(batchNumber => batchNumber!),
                        cancellationToken);

                    // Get already reserved quantities for each batch
                    var remainingQty = inventoryQuantity;
                    foreach (var batch in availableBatches.OrderBy(b => b.ExpiryDate ?? DateTime.MaxValue))
                    {
                        if (remainingQty <= 0) break;

                        var batchKey = BuildReservationStockKey(
                            lineRequest.ItemCode,
                            lineRequest.WarehouseCode,
                            batch.BatchNumber ?? "");

                        var reservedInBatch = reservedByBatch.GetValueOrDefault(batch.BatchNumber ?? "");
                        reservedInBatch += explicitBatchQuantitiesInRequest.GetValueOrDefault(batchKey);
                        reservedInBatch += autoBatchQuantitiesInRequest.GetValueOrDefault(batchKey);

                        var availableInBatch = batch.AvailableQuantity - reservedInBatch;
                        if (availableInBatch > 0)
                        {
                            var allocateQty = Math.Min(remainingQty, availableInBatch);
                            line.BatchAllocations.Add(new StockReservationBatchEntity
                            {
                                ItemCode = lineRequest.ItemCode,
                                BatchNumber = batch.BatchNumber ?? "",
                                WarehouseCode = lineRequest.WarehouseCode,
                                ReservedQuantity = allocateQty,
                                ExpiryDate = batch.ExpiryDate
                            });

                            autoBatchQuantitiesInRequest[batchKey] =
                                autoBatchQuantitiesInRequest.GetValueOrDefault(batchKey) + allocateQty;
                            remainingQty -= allocateQty;
                        }
                    }
                }
            }

            reservation.Lines.Add(line);
        }

        reservation.TotalValue = totalValue;

        // Save to database
        await _dbContext.StockReservations.AddAsync(reservation, cancellationToken);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsDuplicateExternalReference(ex))
        {
            // Two requests carrying the same reference arrived together and both got past the read at
            // the top of this method — a handset that retried on a timeout, or a rep who pressed the
            // button twice. The unique index is what actually settles it; this only turns the loser's
            // exception into the same answer the earlier read would have given.
            //
            // Without it the second caller sees a 500 and the handset, correctly, retries — which is how
            // one sale becomes a queue of failed attempts against a reservation that already exists.
            _dbContext.Entry(reservation).State = EntityState.Detached;

            var winner = await _dbContext.StockReservations
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    r => r.ExternalReferenceId == request.ExternalReferenceId,
                    cancellationToken);

            _logger.LogWarning(
                ex,
                "Two concurrent reservations were created for {ExternalRef}; kept {ReservationId} and " +
                "discarded the duplicate.",
                request.ExternalReferenceId,
                winner?.ReservationId);

            return new StockReservationResponseDto
            {
                Success = true,
                Message = "Existing reservation found for this reference",
                Reservation = winner is null ? null : MapToDto(winner),
                Warnings = ["A concurrent request created this reservation first; using that one."]
            };
        }

        _logger.LogInformation(
            "Stock reservation created successfully. ReservationId: {ReservationId}, ExternalRef: {ExternalRef}, Lines: {LineCount}, TotalValue: {Total}",
            reservation.ReservationId, reservation.ExternalReferenceId, reservation.Lines.Count, totalValue);

        var dto = MapToDto(reservation);

        return new StockReservationResponseDto
        {
            Success = true,
            Message = "Stock reservation created successfully",
            Reservation = dto
        };
    }

    /// <inheritdoc/>
    public async Task<ConfirmReservationResponseDto> ConfirmReservationAsync(
        ConfirmReservationRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Confirming reservation {ReservationId}", request.ReservationId);

        var reservation = await _dbContext.StockReservations
            .Include(r => r.Lines)
                .ThenInclude(l => l.BatchAllocations)
            .FirstOrDefaultAsync(r => r.ReservationId == request.ReservationId, cancellationToken);

        if (reservation == null)
        {
            return new ConfirmReservationResponseDto
            {
                Success = false,
                Message = "Reservation not found",
                ReservationId = request.ReservationId,
                Errors = new List<string> { $"No reservation found with ID '{request.ReservationId}'" }
            };
        }

        if (reservation.Status == ReservationStatus.Confirmed)
        {
            return new ConfirmReservationResponseDto
            {
                Success = true,
                Message = "Reservation was already confirmed",
                ReservationId = request.ReservationId,
                SAPDocEntry = reservation.SAPDocEntry,
                SAPDocNum = reservation.SAPDocNum,
                Errors = new List<string>()
            };
        }

        if (reservation.Status == ReservationStatus.Cancelled)
        {
            return new ConfirmReservationResponseDto
            {
                Success = false,
                Message = "Cannot confirm a cancelled reservation",
                ReservationId = request.ReservationId,
                Errors = new List<string> { $"Reservation was cancelled at {reservation.CancelledAt}: {reservation.CancellationReason}" }
            };
        }

        if (reservation.Status == ReservationStatus.Expired || reservation.ExpiresAt < DateTime.UtcNow)
        {
            reservation.Status = ReservationStatus.Expired;
            await _dbContext.SaveChangesAsync(cancellationToken);

            return new ConfirmReservationResponseDto
            {
                Success = false,
                Message = "Reservation has expired",
                ReservationId = request.ReservationId,
                Errors = new List<string> { $"Reservation expired at {reservation.ExpiresAt}. Please create a new reservation." }
            };
        }

        if (reservation.Status == ReservationStatus.Confirming)
        {
            // Another caller is mid-post. Saying "no" here is the whole point: the honest alternative,
            // waiting for it, would just move the duplicate risk to whichever request timed out first.
            _logger.LogWarning(
                "Reservation {ReservationId} ({ExternalRef}) is already being posted to SAP by another " +
                "request, so this one was refused rather than posting a second invoice for the same sale.",
                reservation.ReservationId,
                reservation.ExternalReferenceId);

            return new ConfirmReservationResponseDto
            {
                Success = false,
                Message = "This sale is already being posted to SAP. Wait for it to finish rather than sending it again.",
                ReservationId = request.ReservationId,
                Errors = new List<string>
                {
                    "A concurrent request holds this reservation. Re-check it in a moment: if it succeeded " +
                    "the sale is posted, and if it failed the reservation returns to Pending and can be retried."
                }
            };
        }

        // Take the reservation before doing anything that reaches SAP.
        //
        // A conditional update, not a read-then-write: it is the WHERE clause that makes this safe. Two
        // requests carrying one van_order both arrive here with the row Pending, and exactly one of them
        // changes a row. The other gets zero rows back and is refused above.
        //
        // Everything after this point — the "is it already in SAP" lookup and the post itself — is the
        // window the guard exists to close. Both requests used to pass that lookup, because neither had
        // posted yet, and both then created an invoice.
        var claimed = await _dbContext.StockReservations
            .Where(row => row.Id == reservation.Id && row.Status == ReservationStatus.Pending)
            .ExecuteUpdateAsync(
                update => update.SetProperty(row => row.Status, ReservationStatus.Confirming),
                cancellationToken);

        if (claimed == 0)
        {
            _logger.LogWarning(
                "Reservation {ReservationId} ({ExternalRef}) was taken by another request between reading " +
                "it and claiming it, so this one did not post.",
                reservation.ReservationId,
                reservation.ExternalReferenceId);

            return new ConfirmReservationResponseDto
            {
                Success = false,
                Message = "This sale is already being posted to SAP. Wait for it to finish rather than sending it again.",
                ReservationId = request.ReservationId,
                Errors = new List<string> { "A concurrent request claimed this reservation first." }
            };
        }

        // The tracked entity still says Pending; the database says Confirming. Keep them in step so the
        // success path's own status write is not a no-op against a stale value.
        reservation.Status = ReservationStatus.Confirming;

        var vanSaleOrder = string.IsNullOrWhiteSpace(reservation.ExternalReferenceId)
            ? null
            : reservation.ExternalReferenceId.Trim();

        // Convert reservation to CreateInvoiceRequest
        var invoiceRequest = new CreateInvoiceRequest
        {
            CardCode = reservation.CardCode,
            DocDate = request.DocDate ?? DateTime.UtcNow.ToString("yyyy-MM-dd"),
            DocDueDate = request.DocDueDate,
            NumAtCard = request.NumAtCard ?? reservation.ExternalReferenceId,
            Comments = request.Comments ?? $"Posted from reservation {reservation.ReservationId}",
            DocCurrency = reservation.Currency,
            SalesPersonCode = request.SalesPersonCode,
            U_Van_saleorder = vanSaleOrder,
            // Also the receipt number the fiscalisation platform is given, so one sale is one document
            // to ZIMRA whether it was stamped on the handset or fiscalised from this invoice.
            MobileInvoiceNumber = vanSaleOrder,
            Lines = reservation.Lines.Select(l => new CreateInvoiceLineRequest
            {
                ItemCode = l.ItemCode,
                Quantity = l.OriginalQuantity,
                UnitPrice = l.UnitPrice,
                WarehouseCode = l.WarehouseCode,
                TaxCode = l.TaxCode,
                DiscountPercent = l.DiscountPercent,
                UoMCode = l.UoMCode,
                CostCentreCode = l.CostCentreCode,
                BatchNumbers = l.BatchAllocations.Select(b => new BatchNumberRequest
                {
                    BatchNumber = b.BatchNumber,
                    Quantity = b.ReservedQuantity,
                    ExpiryDate = b.ExpiryDate
                }).ToList(),
                AutoAllocateBatches = false // We already allocated batches
            }).ToList()
        };

        try
        {
            if (!string.IsNullOrWhiteSpace(vanSaleOrder))
            {
                var existingInvoice = await _sapClient.GetInvoiceByVanSaleOrderAsync(vanSaleOrder, cancellationToken);
                if (existingInvoice != null)
                {
                    reservation.Status = ReservationStatus.Confirmed;
                    reservation.ConfirmedAt ??= DateTime.UtcNow;
                    reservation.SAPDocEntry = existingInvoice.DocEntry;
                    reservation.SAPDocNum = existingInvoice.DocNum;

                    await _dbContext.SaveChangesAsync(cancellationToken);

                    _logger.LogInformation(
                        "Reservation {ReservationId} already exists in SAP as DocEntry {DocEntry}, DocNum {DocNum} for U_Van_saleorder {VanSaleOrder}",
                        reservation.ReservationId,
                        existingInvoice.DocEntry,
                        existingInvoice.DocNum,
                        vanSaleOrder);

                    return new ConfirmReservationResponseDto
                    {
                        Success = true,
                        Message = "Reservation already posted previously; returning existing SAP invoice",
                        ReservationId = reservation.ReservationId,
                        SAPDocEntry = existingInvoice.DocEntry,
                        SAPDocNum = existingInvoice.DocNum,
                        Invoice = existingInvoice.ToDto()
                    };
                }
            }

            // Post to SAP
            var invoice = await _sapClient.CreateInvoiceAsync(invoiceRequest, cancellationToken);

            // Update reservation status
            reservation.Status = ReservationStatus.Confirmed;
            reservation.ConfirmedAt = DateTime.UtcNow;
            reservation.SAPDocEntry = invoice.DocEntry;
            reservation.SAPDocNum = invoice.DocNum;

            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Reservation {ReservationId} confirmed. SAP DocEntry: {DocEntry}, DocNum: {DocNum}",
                reservation.ReservationId, invoice.DocEntry, invoice.DocNum);

            var invoiceDto = invoice.ToDto();
            var (initiatedByUserId, initiatedByUsername) = await ResolveNotificationRecipientAsync(
                reservation.CreatedBy,
                cancellationToken);
            var notificationActionUrl = ResolveNotificationActionUrl(reservation.SourceSystem);

            // Queue fiscalization so fiscal-platform latency does not block the SAP response.
            FiscalizationResult? fiscalizationResult = null;
            if (request.Fiscalize)
            {
                fiscalizationResult = QueueFiscalization(
                    invoiceDto,
                    initiatedByUserId,
                    initiatedByUsername,
                    notificationActionUrl);
            }

            await SendInvoiceCreatedNotificationAsync(
                invoiceDto,
                reservation.ReservationId,
                initiatedByUserId,
                initiatedByUsername,
                notificationActionUrl,
                fiscalizationResult,
                cancellationToken);

            return new ConfirmReservationResponseDto
            {
                Success = true,
                Message = fiscalizationResult?.Queued == true
                    ? "Reservation confirmed successfully; fiscalization is running in the background"
                    : request.Fiscalize
                        ? "Reservation confirmed successfully (fiscalization pending)"
                    : "Reservation confirmed successfully",
                ReservationId = reservation.ReservationId,
                SAPDocEntry = invoice.DocEntry,
                SAPDocNum = invoice.DocNum,
                Invoice = invoiceDto,
                Fiscalization = fiscalizationResult
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to confirm reservation {ReservationId}", reservation.ReservationId);

            if (!SapFailureClassifier.IsTransient(ex, cancellationToken))
            {
                reservation.Status = ReservationStatus.Failed;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            else
            {
                // Hand the claim back, or nothing can ever retry this sale: the reservation would sit in
                // Confirming, every retry would be refused as "already being posted", and the takings
                // would be lost to a transient network failure.
                //
                // Safe to reopen even though the post may have reached SAP and only the reply been lost.
                // The retry asks SAP for this sale's own U_Van_saleorder before posting, and a document
                // that did land is found there and adopted rather than duplicated.
                reservation.Status = ReservationStatus.Pending;
                await _dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogWarning(
                    "Transient SAP failure while confirming reservation {ReservationId}; returned it to " +
                    "Pending for queue fallback or retry.",
                    reservation.ReservationId);
            }

            return new ConfirmReservationResponseDto
            {
                Success = false,
                Message = "Failed to post reservation to SAP",
                ReservationId = reservation.ReservationId,
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <inheritdoc/>
    public async Task<StockReservationResponseDto> CancelReservationAsync(
        CancelReservationRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Cancelling reservation {ReservationId}. Reason: {Reason}",
            request.ReservationId, request.Reason);

        var reservation = await _dbContext.StockReservations
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.ReservationId == request.ReservationId, cancellationToken);

        if (reservation == null)
        {
            return new StockReservationResponseDto
            {
                Success = false,
                Message = "Reservation not found",
                Errors = new List<StockReservationErrorDto>
                {
                    new()
                    {
                        ErrorCode = ReservationErrorCode.ReservationNotFound,
                        Message = $"No reservation found with ID '{request.ReservationId}'"
                    }
                }
            };
        }

        if (reservation.Status == ReservationStatus.Confirmed)
        {
            return new StockReservationResponseDto
            {
                Success = false,
                Message = "Cannot cancel a confirmed reservation",
                Errors = new List<StockReservationErrorDto>
                {
                    new()
                    {
                        ErrorCode = ReservationErrorCode.ReservationAlreadyConfirmed,
                        Message = $"Reservation was already confirmed at {reservation.ConfirmedAt}. SAP DocNum: {reservation.SAPDocNum}"
                    }
                }
            };
        }

        if (reservation.Status == ReservationStatus.Cancelled)
        {
            return new StockReservationResponseDto
            {
                Success = true,
                Message = "Reservation was already cancelled",
                Reservation = MapToDto(reservation)
            };
        }

        // Cancel the reservation
        reservation.Status = ReservationStatus.Cancelled;
        reservation.CancelledAt = DateTime.UtcNow;
        reservation.CancellationReason = request.Reason ?? "Cancelled by user";

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Reservation {ReservationId} cancelled successfully", reservation.ReservationId);

        return new StockReservationResponseDto
        {
            Success = true,
            Message = "Reservation cancelled successfully - stock released",
            Reservation = MapToDto(reservation)
        };
    }

    /// <inheritdoc/>
    public async Task<StockReservationResponseDto> RenewReservationAsync(
        RenewReservationRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Renewing reservation {ReservationId} for {Minutes} minutes",
            request.ReservationId, request.ExtensionMinutes);

        var reservation = await _dbContext.StockReservations
            .Include(r => r.Lines)
                .ThenInclude(l => l.BatchAllocations)
            .FirstOrDefaultAsync(r => r.ReservationId == request.ReservationId, cancellationToken);

        if (reservation == null)
        {
            return new StockReservationResponseDto
            {
                Success = false,
                Message = "Reservation not found",
                Errors = new List<StockReservationErrorDto>
                {
                    new()
                    {
                        ErrorCode = ReservationErrorCode.ReservationNotFound,
                        Message = $"No reservation found with ID '{request.ReservationId}'"
                    }
                }
            };
        }

        if (reservation.Status != ReservationStatus.Pending)
        {
            return new StockReservationResponseDto
            {
                Success = false,
                Message = $"Cannot renew a {reservation.Status.ToLower()} reservation",
                Errors = new List<StockReservationErrorDto>
                {
                    new()
                    {
                        ErrorCode = ReservationErrorCode.ValidationFailed,
                        Message = $"Reservation status is {reservation.Status}. Only pending reservations can be renewed."
                    }
                }
            };
        }

        if (reservation.RenewalCount >= MaxRenewals)
        {
            return new StockReservationResponseDto
            {
                Success = false,
                Message = "Maximum renewal count reached",
                Errors = new List<StockReservationErrorDto>
                {
                    new()
                    {
                        ErrorCode = ReservationErrorCode.ValidationFailed,
                        Message = $"Reservation has been renewed {reservation.RenewalCount} times. Maximum is {MaxRenewals}."
                    }
                },
                Warnings = new List<string> { "Consider confirming or cancelling this reservation" }
            };
        }

        // Extend the expiry
        var newExpiry = DateTime.UtcNow.AddMinutes(request.ExtensionMinutes);
        var maxExpiry = reservation.CreatedAt.AddHours(MaxReservationDurationHours * (reservation.RenewalCount + 2));

        reservation.ExpiresAt = newExpiry > maxExpiry ? maxExpiry : newExpiry;
        reservation.LastRenewedAt = DateTime.UtcNow;
        reservation.RenewalCount++;

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Reservation {ReservationId} renewed. New expiry: {Expiry}, RenewalCount: {Count}",
            reservation.ReservationId, reservation.ExpiresAt, reservation.RenewalCount);

        return new StockReservationResponseDto
        {
            Success = true,
            Message = $"Reservation renewed until {reservation.ExpiresAt:g}",
            Reservation = MapToDto(reservation)
        };
    }

    /// <inheritdoc/>
    public async Task<StockReservationDto?> GetReservationAsync(
        string reservationId,
        CancellationToken cancellationToken = default)
    {
        var reservation = await _dbContext.StockReservations
            .Include(r => r.Lines)
                .ThenInclude(l => l.BatchAllocations)
            .FirstOrDefaultAsync(r => r.ReservationId == reservationId, cancellationToken);

        return reservation != null ? MapToDto(reservation) : null;
    }

    /// <inheritdoc/>
    public async Task<StockReservationDto?> GetReservationByExternalReferenceAsync(
        string externalReferenceId,
        CancellationToken cancellationToken = default)
    {
        var reservation = await _dbContext.StockReservations
            .Include(r => r.Lines)
                .ThenInclude(l => l.BatchAllocations)
            .FirstOrDefaultAsync(r => r.ExternalReferenceId == externalReferenceId, cancellationToken);

        return reservation != null ? MapToDto(reservation) : null;
    }

    /// <inheritdoc/>
    public async Task<ReservationListResponseDto> ListReservationsAsync(
        ReservationQueryParams query,
        CancellationToken cancellationToken = default)
    {
        var queryable = _dbContext.StockReservations
            .Include(r => r.Lines)
            .AsQueryable();

        if (!string.IsNullOrEmpty(query.SourceSystem))
        {
            queryable = queryable.Where(r => r.SourceSystem == query.SourceSystem);
        }

        if (!string.IsNullOrEmpty(query.Status))
        {
            queryable = queryable.Where(r => r.Status == query.Status);
        }

        if (!string.IsNullOrEmpty(query.CardCode))
        {
            queryable = queryable.Where(r => r.CardCode == query.CardCode);
        }

        if (!string.IsNullOrEmpty(query.ExternalReferenceId))
        {
            queryable = queryable.Where(r => r.ExternalReferenceId.Contains(query.ExternalReferenceId));
        }

        if (query.ActiveOnly)
        {
            queryable = queryable.Where(r => r.Status == ReservationStatus.Pending && r.ExpiresAt > DateTime.UtcNow);
        }

        var totalCount = await queryable.CountAsync(cancellationToken);
        var totalPages = (int)Math.Ceiling(totalCount / (double)query.PageSize);

        var reservations = await queryable
            .OrderByDescending(r => r.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new ReservationListResponseDto
        {
            Page = query.Page,
            PageSize = query.PageSize,
            TotalCount = totalCount,
            TotalPages = totalPages,
            HasMore = query.Page < totalPages,
            Reservations = reservations.Select(MapToDto).ToList()
        };
    }

    /// <inheritdoc/>
    public async Task<decimal> GetReservedQuantityAsync(
        string itemCode,
        string warehouseCode,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.StockReservationLines
            .Where(l => l.ItemCode == itemCode
                && l.WarehouseCode == warehouseCode
                && l.Reservation.Status == ReservationStatus.Pending
                && l.Reservation.ExpiresAt > DateTime.UtcNow)
            .SumAsync(l => l.ReservedQuantity, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<decimal> GetReservedBatchQuantityAsync(
        string itemCode,
        string warehouseCode,
        string batchNumber,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.StockReservationBatches
            .Where(b => b.ItemCode == itemCode
                && b.WarehouseCode == warehouseCode
                && b.BatchNumber == batchNumber
                && b.ReservationLine.Reservation.Status == ReservationStatus.Pending
                && b.ReservationLine.Reservation.ExpiresAt > DateTime.UtcNow)
            .SumAsync(b => b.ReservedQuantity, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, decimal>> GetReservedBatchQuantitiesAsync(
        string itemCode,
        string warehouseCode,
        IEnumerable<string> batchNumbers,
        CancellationToken cancellationToken = default)
    {
        var normalizedBatchNumbers = batchNumbers
            .Where(batchNumber => !string.IsNullOrWhiteSpace(batchNumber))
            .Select(batchNumber => batchNumber.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedBatchNumbers.Count == 0)
        {
            return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        }

        var reservedByBatch = await _dbContext.StockReservationBatches
            .Where(batch => batch.ItemCode == itemCode
                && batch.WarehouseCode == warehouseCode
                && normalizedBatchNumbers.Contains(batch.BatchNumber)
                && batch.ReservationLine.Reservation.Status == ReservationStatus.Pending
                && batch.ReservationLine.Reservation.ExpiresAt > DateTime.UtcNow)
            .GroupBy(batch => batch.BatchNumber)
            .Select(group => new
            {
                BatchNumber = group.Key,
                Quantity = group.Sum(batch => batch.ReservedQuantity)
            })
            .ToListAsync(cancellationToken);

        return reservedByBatch.ToDictionary(
            entry => entry.BatchNumber,
            entry => entry.Quantity,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public async Task<ReservedStockSummaryDto> GetReservedStockSummaryAsync(
        string itemCode,
        string warehouseCode,
        CancellationToken cancellationToken = default)
    {
        var reservedQty = await GetReservedQuantityAsync(itemCode, warehouseCode, cancellationToken);

        // Ask for the one item rather than scanning the warehouse to pick it out of the result.
        var stockItems = await _sapClient.GetStockQuantitiesForItemsInWarehouseAsync(
            warehouseCode, [itemCode], cancellationToken);
        var stockItem = stockItems.FirstOrDefault(s =>
            string.Equals(s.ItemCode, itemCode, StringComparison.OrdinalIgnoreCase));
        var physicalQty = stockItem?.InStock ?? 0;

        // Get batch-level reservations
        var batchReservations = await _dbContext.StockReservationBatches
            .Where(b => b.ItemCode == itemCode
                && b.WarehouseCode == warehouseCode
                && b.ReservationLine.Reservation.Status == ReservationStatus.Pending
                && b.ReservationLine.Reservation.ExpiresAt > DateTime.UtcNow)
            .GroupBy(b => b.BatchNumber)
            .Select(g => new ReservedStockBatchSummaryDto
            {
                BatchNumber = g.Key,
                TotalReservedQuantity = g.Sum(b => b.ReservedQuantity),
                ExpiryDate = g.Max(b => b.ExpiryDate)
            })
            .ToListAsync(cancellationToken);

        // Get batch physical quantities
        var batches = await _batchValidation.GetAvailableBatchesAsync(
            itemCode, warehouseCode, BatchAllocationStrategy.FEFO, cancellationToken);

        foreach (var batchRes in batchReservations)
        {
            var batch = batches.FirstOrDefault(b => b.BatchNumber == batchRes.BatchNumber);
            if (batch != null)
            {
                batchRes.PhysicalQuantity = batch.AvailableQuantity;
                batchRes.AvailableQuantity = batch.AvailableQuantity - batchRes.TotalReservedQuantity;
            }
        }

        var activeReservationCount = await _dbContext.StockReservationLines
            .Where(l => l.ItemCode == itemCode
                && l.WarehouseCode == warehouseCode
                && l.Reservation.Status == ReservationStatus.Pending
                && l.Reservation.ExpiresAt > DateTime.UtcNow)
            .Select(l => l.ReservationId)
            .Distinct()
            .CountAsync(cancellationToken);

        return new ReservedStockSummaryDto
        {
            ItemCode = itemCode,
            WarehouseCode = warehouseCode,
            TotalReservedQuantity = reservedQty,
            PhysicalQuantity = physicalQty,
            AvailableQuantity = physicalQty - reservedQty,
            ActiveReservationCount = activeReservationCount,
            BatchReservations = batchReservations
        };
    }

    /// <inheritdoc/>
    /// <summary>
    /// Whether a failed save was the unique index on <c>ExternalReferenceId</c> rejecting a duplicate.
    /// </summary>
    /// <remarks>
    /// Matched on the index name rather than on a Postgres error code alone, because this table carries
    /// several unique constraints and swallowing the wrong one would hide a real fault as a duplicate.
    /// </remarks>
    private static bool IsDuplicateExternalReference(DbUpdateException exception) =>
        exception.InnerException?.Message.Contains(
            "IX_StockReservations_ExternalReferenceId",
            StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// How long a reservation may sit claimed for posting before the claim is treated as abandoned.
    /// </summary>
    /// <remarks>
    /// Comfortably longer than any real post. A SAP invoice takes seconds; the only way a claim outlives
    /// this is a process that died holding it, and then the sale needs reopening or it is lost.
    /// Reopening one that is genuinely still in flight would be the worse mistake, hence the margin.
    /// </remarks>
    private static readonly TimeSpan AbandonedClaimTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Returns reservations whose posting attempt died mid-flight to <c>Pending</c>, so they can be tried
    /// again.
    /// </summary>
    /// <remarks>
    /// A claim is taken in the database and released in a <c>finally</c>-shaped code path, which covers
    /// every failure except the one that skips the code entirely — an app-pool recycle, a killed process,
    /// a machine that goes away. Without this the reservation stays claimed for ever, every retry is
    /// refused as "already being posted", and a real sale is silently never invoiced.
    ///
    /// Reopening is safe for the same reason a transient failure reopens: the retry asks SAP for the
    /// sale's own reference before posting, so a document that did land is adopted rather than duplicated.
    /// </remarks>
    private async Task ReopenAbandonedClaimsAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow - AbandonedClaimTimeout;

        var abandoned = await _dbContext.StockReservations
            .Where(r => r.Status == ReservationStatus.Confirming && r.CreatedAt <= cutoff)
            .ExecuteUpdateAsync(
                update => update.SetProperty(r => r.Status, ReservationStatus.Pending),
                cancellationToken);

        if (abandoned > 0)
        {
            _logger.LogWarning(
                "Reopened {Count} reservation(s) left mid-post for more than {Minutes} minutes — a process " +
                "died holding them. They will be retried, and any invoice that did reach SAP will be found " +
                "by its reference rather than posted twice.",
                abandoned,
                AbandonedClaimTimeout.TotalMinutes);
        }
    }

    public async Task<int> ExpireReservationsAsync(CancellationToken cancellationToken = default)
    {
        await ReopenAbandonedClaimsAsync(cancellationToken);

        var expiredReservations = await _dbContext.StockReservations
            .Where(r => r.Status == ReservationStatus.Pending && r.ExpiresAt <= DateTime.UtcNow)
            .ToListAsync(cancellationToken);

        foreach (var reservation in expiredReservations)
        {
            reservation.Status = ReservationStatus.Expired;
            _logger.LogInformation(
                "Reservation {ReservationId} ({ExternalRef}) expired. Stock released.",
                reservation.ReservationId, reservation.ExternalReferenceId);
        }

        if (expiredReservations.Count > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Expired {Count} reservations", expiredReservations.Count);
        }

        return expiredReservations.Count;
    }

    /// <inheritdoc/>
    public async Task<(bool IsValid, List<StockReservationErrorDto> Errors)> ValidateStockAvailabilityAsync(
        List<CreateStockReservationLineRequest> lines,
        string? excludeReservationId = null,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<StockReservationErrorDto>();
        var aggregateLines = new List<(CreateStockReservationLineRequest Line, decimal InventoryQuantity)>();

        // One stock read per warehouse, for the item codes actually asked about. This used to be a
        // whole-warehouse read *per line*: GetStockQuantitiesInWarehouseAsync scans OITM joined to
        // OITW for every item in the warehouse and pages it 500 rows at a time, so a twenty-line
        // request against a five-thousand-item warehouse spent about 220 SAP round-trips answering
        // twenty single-item questions.
        var stockByWarehouse = await LoadRequestedStockAsync(lines, cancellationToken);

        // Available batches depend only on the item and warehouse, but were re-fetched once per
        // requested batch number, and again in the aggregate pass below.
        var batchesByItemAndWarehouse = new Dictionary<string, List<AvailableBatchDto>>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            var stockItem = FindRequestedStock(stockByWarehouse, line.WarehouseCode, line.ItemCode);

            if (stockItem == null)
            {
                errors.Add(new StockReservationErrorDto
                {
                    ErrorCode = ReservationErrorCode.ItemNotFound,
                    LineNumber = line.LineNum,
                    ItemCode = line.ItemCode,
                    WarehouseCode = line.WarehouseCode,
                    RequestedQuantity = line.Quantity,
                    AvailableQuantity = 0,
                    Message = $"Item '{line.ItemCode}' not found in warehouse '{line.WarehouseCode}'",
                    SuggestedAction = "Verify item code and warehouse"
                });
                continue;
            }

            // Convert quantity to inventory UoM
            decimal inventoryQuantity = line.Quantity;
            if (!string.IsNullOrEmpty(line.UoMCode))
            {
                var uomInfo = await _batchValidation.GetUoMConversionAsync(
                    line.ItemCode, line.UoMCode, null, cancellationToken);
                if (uomInfo != null && uomInfo.ConversionFactor > 0)
                {
                    inventoryQuantity = line.Quantity * uomInfo.ConversionFactor;
                }
            }

            aggregateLines.Add((line, inventoryQuantity));

            // Get reserved quantity (excluding the current reservation if renewing)
            var reservedQty = await _dbContext.StockReservationLines
                .Where(l => l.ItemCode == line.ItemCode
                    && l.WarehouseCode == line.WarehouseCode
                    && l.Reservation.Status == ReservationStatus.Pending
                    && l.Reservation.ExpiresAt > DateTime.UtcNow
                    && (excludeReservationId == null || l.Reservation.ReservationId != excludeReservationId))
                .SumAsync(l => l.ReservedQuantity, cancellationToken);

            var issuableQty = stockItem.InStock - stockItem.Committed;
            var availableQty = issuableQty - reservedQty;

            if (inventoryQuantity > availableQty)
            {
                errors.Add(new StockReservationErrorDto
                {
                    ErrorCode = ReservationErrorCode.InsufficientStock,
                    LineNumber = line.LineNum,
                    ItemCode = line.ItemCode,
                    WarehouseCode = line.WarehouseCode,
                    RequestedQuantity = inventoryQuantity,
                    AvailableQuantity = availableQty,
                    Message = $"Insufficient stock for '{line.ItemCode}' in '{line.WarehouseCode}'. Requested: {inventoryQuantity:N2}, Available: {availableQty:N2} (On hand less committed: {issuableQty:N2}, Reserved: {reservedQty:N2})",
                    SuggestedAction = $"Reduce quantity to {availableQty:N2} or choose a different warehouse"
                });
            }

            // Validate batch-level availability for batch-managed items
            var isBatchManaged = await _batchValidation.IsBatchManagedItemAsync(line.ItemCode, cancellationToken);
            if (isBatchManaged && line.BatchNumbers != null && line.BatchNumbers.Count > 0)
            {
                var batches = await GetAvailableBatchesOnceAsync(
                    batchesByItemAndWarehouse, line.ItemCode, line.WarehouseCode, cancellationToken);

                foreach (var batchReq in line.BatchNumbers)
                {
                    var batch = batches.FirstOrDefault(b => b.BatchNumber == batchReq.BatchNumber);

                    if (batch == null)
                    {
                        errors.Add(new StockReservationErrorDto
                        {
                            ErrorCode = ReservationErrorCode.BatchNotFound,
                            LineNumber = line.LineNum,
                            ItemCode = line.ItemCode,
                            WarehouseCode = line.WarehouseCode,
                            BatchNumber = batchReq.BatchNumber,
                            RequestedQuantity = batchReq.Quantity,
                            AvailableQuantity = 0,
                            Message = $"Batch '{batchReq.BatchNumber}' not found for item '{line.ItemCode}' in warehouse '{line.WarehouseCode}'",
                            SuggestedAction = "Verify batch number or use auto-allocation"
                        });
                        continue;
                    }

                    var reservedBatchQty = await GetReservedBatchQuantityAsync(
                        line.ItemCode, line.WarehouseCode, batchReq.BatchNumber, cancellationToken);
                    var availableBatchQty = batch.AvailableQuantity - reservedBatchQty;

                    if (batchReq.Quantity > availableBatchQty)
                    {
                        errors.Add(new StockReservationErrorDto
                        {
                            ErrorCode = ReservationErrorCode.InsufficientBatchStock,
                            LineNumber = line.LineNum,
                            ItemCode = line.ItemCode,
                            WarehouseCode = line.WarehouseCode,
                            BatchNumber = batchReq.BatchNumber,
                            RequestedQuantity = batchReq.Quantity,
                            AvailableQuantity = availableBatchQty,
                            Message = $"Insufficient stock in batch '{batchReq.BatchNumber}'. Requested: {batchReq.Quantity:N2}, Available: {availableBatchQty:N2}",
                            SuggestedAction = $"Reduce quantity to {availableBatchQty:N2} or use auto-allocation"
                        });
                    }
                }
            }
        }

        var aggregateStockRequests = aggregateLines
            .GroupBy(lineInfo => BuildReservationStockKey(lineInfo.Line.ItemCode, lineInfo.Line.WarehouseCode))
            .Where(group => group.Count() > 1);

        foreach (var stockRequestGroup in aggregateStockRequests)
        {
            var firstLine = stockRequestGroup.First().Line;
            var requestedQuantity = stockRequestGroup.Sum(lineInfo => lineInfo.InventoryQuantity);
            var stockItem = FindRequestedStock(stockByWarehouse, firstLine.WarehouseCode, firstLine.ItemCode);

            if (stockItem == null)
                continue;

            var reservedQty = await _dbContext.StockReservationLines
                .Where(reservationLine => reservationLine.ItemCode == firstLine.ItemCode
                    && reservationLine.WarehouseCode == firstLine.WarehouseCode
                    && reservationLine.Reservation.Status == ReservationStatus.Pending
                    && reservationLine.Reservation.ExpiresAt > DateTime.UtcNow
                    && (excludeReservationId == null || reservationLine.Reservation.ReservationId != excludeReservationId))
                .SumAsync(reservationLine => reservationLine.ReservedQuantity, cancellationToken);

            var issuableQty = stockItem.InStock - stockItem.Committed;
            var availableQty = issuableQty - reservedQty;

            if (requestedQuantity > availableQty + 0.0001m)
            {
                var lineNumbers = string.Join(", ", stockRequestGroup
                    .Select(lineInfo => lineInfo.Line.LineNum)
                    .OrderBy(lineNumber => lineNumber));
                errors.Add(new StockReservationErrorDto
                {
                    ErrorCode = ReservationErrorCode.InsufficientStock,
                    LineNumber = stockRequestGroup.Max(lineInfo => lineInfo.Line.LineNum),
                    ItemCode = firstLine.ItemCode,
                    WarehouseCode = firstLine.WarehouseCode,
                    RequestedQuantity = requestedQuantity,
                    AvailableQuantity = availableQty,
                    Message = $"Combined requested stock for '{firstLine.ItemCode}' in '{firstLine.WarehouseCode}' across lines {lineNumbers} is {requestedQuantity:N2}, available {availableQty:N2} (On hand less committed: {issuableQty:N2}, Reserved: {reservedQty:N2})",
                    SuggestedAction = $"Reduce combined quantity to {availableQty:N2} or choose a different warehouse"
                });
            }
        }

        var explicitBatchRequests = aggregateLines
            .Where(lineInfo => lineInfo.Line.BatchNumbers is { Count: > 0 })
            .SelectMany(lineInfo => lineInfo.Line.BatchNumbers!.Select(batch => new
            {
                lineInfo.Line,
                BatchNumber = batch.BatchNumber,
                batch.Quantity
            }))
            .Where(batchInfo => !string.IsNullOrWhiteSpace(batchInfo.BatchNumber))
            .GroupBy(batchInfo => BuildReservationStockKey(
                batchInfo.Line.ItemCode,
                batchInfo.Line.WarehouseCode,
                batchInfo.BatchNumber))
            .Where(group => group.Count() > 1);

        foreach (var batchRequestGroup in explicitBatchRequests)
        {
            var firstBatchRequest = batchRequestGroup.First();
            var requestedQuantity = batchRequestGroup.Sum(batchInfo => batchInfo.Quantity);
            var batches = await GetAvailableBatchesOnceAsync(
                batchesByItemAndWarehouse,
                firstBatchRequest.Line.ItemCode,
                firstBatchRequest.Line.WarehouseCode,
                cancellationToken);
            var batch = batches.FirstOrDefault(availableBatch =>
                string.Equals(availableBatch.BatchNumber, firstBatchRequest.BatchNumber, StringComparison.OrdinalIgnoreCase));

            if (batch == null)
                continue;

            var reservedBatchQty = await GetReservedBatchQuantityAsync(
                firstBatchRequest.Line.ItemCode,
                firstBatchRequest.Line.WarehouseCode,
                firstBatchRequest.BatchNumber!,
                cancellationToken);
            var availableBatchQty = batch.AvailableQuantity - reservedBatchQty;

            if (requestedQuantity > availableBatchQty + 0.0001m)
            {
                var lineNumbers = string.Join(", ", batchRequestGroup
                    .Select(batchInfo => batchInfo.Line.LineNum)
                    .OrderBy(lineNumber => lineNumber));
                errors.Add(new StockReservationErrorDto
                {
                    ErrorCode = ReservationErrorCode.InsufficientBatchStock,
                    LineNumber = batchRequestGroup.Max(batchInfo => batchInfo.Line.LineNum),
                    ItemCode = firstBatchRequest.Line.ItemCode,
                    WarehouseCode = firstBatchRequest.Line.WarehouseCode,
                    BatchNumber = firstBatchRequest.BatchNumber,
                    RequestedQuantity = requestedQuantity,
                    AvailableQuantity = availableBatchQty,
                    Message = $"Combined requested stock in batch '{firstBatchRequest.BatchNumber}' across lines {lineNumbers} is {requestedQuantity:N2}, available {availableBatchQty:N2}",
                    SuggestedAction = $"Reduce combined batch quantity to {availableBatchQty:N2} or use auto-allocation"
                });
            }
        }

        return (errors.Count == 0, errors);
    }

    private static Dictionary<string, decimal> BuildExplicitBatchQuantityLookup(
        List<CreateStockReservationLineRequest> lines)
    {
        return lines
            .Where(line => line.BatchNumbers is { Count: > 0 })
            .SelectMany(line => line.BatchNumbers!.Select(batch => new
            {
                Key = BuildReservationStockKey(line.ItemCode, line.WarehouseCode, batch.BatchNumber),
                batch.Quantity
            }))
            .GroupBy(batch => batch.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(batch => batch.Quantity),
                StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildReservationStockKey(params string?[] parts) =>
        string.Join("|", parts.Select(part => (part ?? string.Empty).Trim().ToUpperInvariant()));

    /// <summary>
    /// Reads current stock for every (warehouse, item) pair the request mentions, one SAP call per
    /// warehouse.
    /// </summary>
    /// <remarks>
    /// Item codes go to SAP exactly as the caller supplied them and the result is indexed
    /// case-insensitively — the same pairing <c>SAPServiceLayerClient.ValidateStockAvailabilityAsync</c>
    /// uses for this query. A warehouse SAP cannot answer for is left absent rather than empty, so
    /// its lines report "not found" as they did before.
    /// </remarks>
    private async Task<Dictionary<string, Dictionary<string, StockQuantityDto>>> LoadRequestedStockAsync(
        List<CreateStockReservationLineRequest> lines,
        CancellationToken cancellationToken)
    {
        var stockByWarehouse = new Dictionary<string, Dictionary<string, StockQuantityDto>>(StringComparer.OrdinalIgnoreCase);

        var warehouseGroups = lines
            .Where(line => !string.IsNullOrWhiteSpace(line.WarehouseCode)
                && !string.IsNullOrWhiteSpace(line.ItemCode))
            .GroupBy(line => line.WarehouseCode, StringComparer.OrdinalIgnoreCase);

        foreach (var warehouseGroup in warehouseGroups)
        {
            var itemCodes = warehouseGroup
                .Select(line => line.ItemCode)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var stockItems = await _sapClient.GetStockQuantitiesForItemsInWarehouseAsync(
                warehouseGroup.Key,
                itemCodes,
                cancellationToken);

            var byItemCode = new Dictionary<string, StockQuantityDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var stockItem in stockItems)
            {
                if (!string.IsNullOrWhiteSpace(stockItem.ItemCode))
                {
                    byItemCode[stockItem.ItemCode] = stockItem;
                }
            }

            stockByWarehouse[warehouseGroup.Key] = byItemCode;
        }

        return stockByWarehouse;
    }

    private static StockQuantityDto? FindRequestedStock(
        Dictionary<string, Dictionary<string, StockQuantityDto>> stockByWarehouse,
        string? warehouseCode,
        string? itemCode)
    {
        if (string.IsNullOrWhiteSpace(warehouseCode) || string.IsNullOrWhiteSpace(itemCode))
        {
            return null;
        }

        return stockByWarehouse.TryGetValue(warehouseCode, out var byItemCode)
            && byItemCode.TryGetValue(itemCode, out var stockItem)
                ? stockItem
                : null;
    }

    /// <summary>
    /// Returns the available batches for one item in one warehouse, reading them at most once per
    /// validation regardless of how many batch numbers the request names.
    /// </summary>
    private async Task<List<AvailableBatchDto>> GetAvailableBatchesOnceAsync(
        Dictionary<string, List<AvailableBatchDto>> cache,
        string itemCode,
        string warehouseCode,
        CancellationToken cancellationToken)
    {
        var key = BuildReservationStockKey(itemCode, warehouseCode);
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var batches = await _batchValidation.GetAvailableBatchesAsync(
            itemCode, warehouseCode, BatchAllocationStrategy.FEFO, cancellationToken);
        cache[key] = batches;
        return batches;
    }

    private FiscalizationResult QueueFiscalization(
        InvoiceDto invoice,
        Guid? userId,
        string? username,
        string notificationActionUrl)
    {
        var queued = _fiscalizationQueue.TryQueue(new InvoiceFiscalizationWorkItem(
            invoice,
            new CustomerFiscalDetails { CustomerName = invoice.CardName },
            userId,
            username,
            notificationActionUrl));

        if (queued)
        {
            _logger.LogInformation(
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

        _logger.LogError(
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

    private async Task<(Guid? UserId, string? Username)> ResolveNotificationRecipientAsync(
        string? createdBy,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(createdBy))
        {
            return (null, null);
        }

        var normalizedCreatedBy = createdBy.Trim();

        if (Guid.TryParse(normalizedCreatedBy, out var userId))
        {
            var username = await _dbContext.Users
                .AsNoTracking()
                .Where(user => user.Id == userId)
                .Select(user => user.Username)
                .FirstOrDefaultAsync(cancellationToken);

            return (userId, username);
        }

        var user = await _dbContext.Users
            .AsNoTracking()
            .WhereUsernameMatches(normalizedCreatedBy)
            .Select(candidate => new { candidate.Id, candidate.Username })
            .FirstOrDefaultAsync(cancellationToken);

        return user is null
            ? (null, normalizedCreatedBy)
            : (user.Id, user.Username);
    }

    private async Task SendInvoiceCreatedNotificationAsync(
        InvoiceDto invoice,
        string reservationId,
        Guid? userId,
        string? username,
        string notificationActionUrl,
        FiscalizationResult? fiscalizationResult,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        try
        {
            var notification = WorkflowNotificationFactory.CreateInvoiceCreatedNotification(
                userId,
                username,
                invoice,
                reservationId,
                notificationActionUrl,
                fiscalizationResult);

            await _notificationService.CreateNotificationAsync(notification, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to notify user {Username} about SAP invoice {DocNum}",
                username,
                invoice.DocNum);
        }
    }

    private static string ResolveNotificationActionUrl(string? sourceSystem)
        => string.Equals(sourceSystem, "KefalosVanSales", StringComparison.OrdinalIgnoreCase)
            ? "/mobile-drafts"
            : "/invoices";

    private StockReservationDto MapToDto(StockReservationEntity entity)
    {
        return new StockReservationDto
        {
            Id = entity.Id,
            ReservationId = entity.ReservationId,
            ExternalReferenceId = entity.ExternalReferenceId,
            SourceSystem = entity.SourceSystem,
            DocumentType = entity.DocumentType,
            CardCode = entity.CardCode,
            CardName = entity.CardName,
            TotalValue = entity.TotalValue,
            Currency = entity.Currency,
            Status = entity.Status,
            CreatedAt = entity.CreatedAt,
            ExpiresAt = entity.ExpiresAt,
            ConfirmedAt = entity.ConfirmedAt,
            SAPDocEntry = entity.SAPDocEntry,
            SAPDocNum = entity.SAPDocNum,
            CancelledAt = entity.CancelledAt,
            CancellationReason = entity.CancellationReason,
            RenewalCount = entity.RenewalCount,
            CreatedBy = entity.CreatedBy,
            Lines = entity.Lines.Select(l => new StockReservationLineDto
            {
                Id = l.Id,
                LineNum = l.LineNum,
                ItemCode = l.ItemCode,
                ItemDescription = l.ItemDescription,
                ReservedQuantity = l.ReservedQuantity,
                OriginalQuantity = l.OriginalQuantity,
                UoMCode = l.UoMCode,
                WarehouseCode = l.WarehouseCode,
                UnitPrice = l.UnitPrice,
                LineTotal = l.LineTotal,
                TaxCode = l.TaxCode,
                DiscountPercent = l.DiscountPercent,
                BatchAllocations = l.BatchAllocations.Select(b => new StockReservationBatchDto
                {
                    Id = b.Id,
                    ItemCode = b.ItemCode,
                    BatchNumber = b.BatchNumber,
                    WarehouseCode = b.WarehouseCode,
                    ReservedQuantity = b.ReservedQuantity,
                    ExpiryDate = b.ExpiryDate
                }).ToList()
            }).ToList()
        };
    }
}
