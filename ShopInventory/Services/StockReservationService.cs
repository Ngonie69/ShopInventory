using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
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
    private readonly IFiscalizationService _fiscalizationService;
    private readonly ILogger<StockReservationService> _logger;

    private const int MaxRenewals = 10;
    private const int MaxReservationDurationHours = 24;

    public StockReservationService(
        ApplicationDbContext dbContext,
        ISAPServiceLayerClient sapClient,
        IBatchInventoryValidationService batchValidation,
        IInventoryLockService lockService,
        IFiscalizationService fiscalizationService,
        ILogger<StockReservationService> logger)
    {
        _dbContext = dbContext;
        _sapClient = sapClient;
        _batchValidation = batchValidation;
        _lockService = lockService;
        _fiscalizationService = fiscalizationService;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<StockReservationResponseDto> CreateReservationAsync(
        CreateStockReservationRequest request,
        string? createdBy = null,
        CancellationToken cancellationToken = default)
    {
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
            Currency = request.Currency,
            Status = ReservationStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(Math.Min(request.ReservationDurationMinutes, MaxReservationDurationHours * 60)),
            CreatedBy = createdBy,
            Metadata = request.Metadata != null ? JsonSerializer.Serialize(request.Metadata) : null
        };

        decimal totalValue = 0;

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
                DiscountPercent = lineRequest.DiscountPercent
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

                    // Get already reserved quantities for each batch
                    var remainingQty = inventoryQuantity;
                    foreach (var batch in availableBatches.OrderBy(b => b.ExpiryDate ?? DateTime.MaxValue))
                    {
                        if (remainingQty <= 0) break;

                        var reservedInBatch = await GetReservedBatchQuantityAsync(
                            lineRequest.ItemCode, lineRequest.WarehouseCode, batch.BatchNumber ?? "", cancellationToken);

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
        await _dbContext.SaveChangesAsync(cancellationToken);

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
                Success = false,
                Message = "Reservation has already been confirmed",
                ReservationId = request.ReservationId,
                SAPDocEntry = reservation.SAPDocEntry,
                SAPDocNum = reservation.SAPDocNum,
                Errors = new List<string> { "This reservation was already posted to SAP" }
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
            Lines = reservation.Lines.Select(l => new CreateInvoiceLineRequest
            {
                ItemCode = l.ItemCode,
                Quantity = l.OriginalQuantity,
                UnitPrice = l.UnitPrice,
                WarehouseCode = l.WarehouseCode,
                TaxCode = l.TaxCode,
                DiscountPercent = l.DiscountPercent,
                UoMCode = l.UoMCode,
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

            // Fiscalize if requested
            FiscalizationResult? fiscalizationResult = null;
            if (request.Fiscalize)
            {
                try
                {
                    fiscalizationResult = await _fiscalizationService.FiscalizeInvoiceAsync(
                        invoice.ToDto(),
                        new CustomerFiscalDetails { CustomerName = reservation.CardName },
                        cancellationToken);

                    if (fiscalizationResult.Success)
                    {
                        _logger.LogInformation("Invoice {DocNum} fiscalized successfully", invoice.DocNum);
                    }
                    else
                    {
                        _logger.LogWarning("Invoice {DocNum} fiscalization failed: {Message}", invoice.DocNum, fiscalizationResult.Message);
                    }
                }
                catch (Exception fiscalEx)
                {
                    _logger.LogError(fiscalEx, "Error during fiscalization of invoice {DocNum}", invoice.DocNum);
                    fiscalizationResult = new FiscalizationResult
                    {
                        Success = false,
                        Message = "Fiscalization error",
                        ErrorDetails = fiscalEx.Message
                    };
                }
            }

            return new ConfirmReservationResponseDto
            {
                Success = true,
                Message = fiscalizationResult?.Success == true
                    ? "Reservation confirmed and fiscalized successfully"
                    : "Reservation confirmed successfully",
                ReservationId = reservation.ReservationId,
                SAPDocEntry = invoice.DocEntry,
                SAPDocNum = invoice.DocNum,
                Invoice = invoice.ToDto(),
                Fiscalization = fiscalizationResult
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to confirm reservation {ReservationId}", reservation.ReservationId);

            reservation.Status = ReservationStatus.Failed;
            await _dbContext.SaveChangesAsync(cancellationToken);

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

    /// <inheritdoc/>
    public async Task<ReservedStockSummaryDto> GetReservedStockSummaryAsync(
        string itemCode,
        string warehouseCode,
        CancellationToken cancellationToken = default)
    {
        var reservedQty = await GetReservedQuantityAsync(itemCode, warehouseCode, cancellationToken);

        // Get physical stock from SAP or local cache
        var stockItems = await _sapClient.GetStockQuantitiesInWarehouseAsync(warehouseCode, cancellationToken);
        var stockItem = stockItems.FirstOrDefault(s => s.ItemCode == itemCode);
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
    public async Task<int> ExpireReservationsAsync(CancellationToken cancellationToken = default)
    {
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

        foreach (var line in lines)
        {
            // Get physical stock
            var stockItems = await _sapClient.GetStockQuantitiesInWarehouseAsync(line.WarehouseCode, cancellationToken);
            var stockItem = stockItems.FirstOrDefault(s => s.ItemCode == line.ItemCode);

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

            // Get reserved quantity (excluding the current reservation if renewing)
            var reservedQty = await _dbContext.StockReservationLines
                .Where(l => l.ItemCode == line.ItemCode
                    && l.WarehouseCode == line.WarehouseCode
                    && l.Reservation.Status == ReservationStatus.Pending
                    && l.Reservation.ExpiresAt > DateTime.UtcNow
                    && (excludeReservationId == null || l.Reservation.ReservationId != excludeReservationId))
                .SumAsync(l => l.ReservedQuantity, cancellationToken);

            var availableQty = stockItem.Available - reservedQty;

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
                    Message = $"Insufficient stock for '{line.ItemCode}' in '{line.WarehouseCode}'. Requested: {inventoryQuantity:N2}, Available: {availableQty:N2} (Physical: {stockItem.InStock:N2}, Reserved: {reservedQty:N2})",
                    SuggestedAction = $"Reduce quantity to {availableQty:N2} or choose a different warehouse"
                });
            }

            // Validate batch-level availability for batch-managed items
            var isBatchManaged = await _batchValidation.IsBatchManagedItemAsync(line.ItemCode, cancellationToken);
            if (isBatchManaged && line.BatchNumbers != null && line.BatchNumbers.Count > 0)
            {
                foreach (var batchReq in line.BatchNumbers)
                {
                    var batches = await _batchValidation.GetAvailableBatchesAsync(
                        line.ItemCode, line.WarehouseCode, BatchAllocationStrategy.FEFO, cancellationToken);
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

        return (errors.Count == 0, errors);
    }

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
