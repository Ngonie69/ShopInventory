using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using System.Security.Claims;

namespace ShopInventory.Controllers;

/// <summary>
/// API Controller for desktop application integration.
/// Provides endpoints for stock reservations and invoice management
/// to ensure server-side stock control and prevent negative quantities.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class DesktopIntegrationController : ControllerBase
{
    private readonly IStockReservationService _reservationService;
    private readonly IInvoiceQueueService _queueService;
    private readonly IInventoryTransferQueueService _transferQueueService;
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly IBatchInventoryValidationService _batchValidation;
    private readonly ILogger<DesktopIntegrationController> _logger;

    public DesktopIntegrationController(
        IStockReservationService reservationService,
        IInvoiceQueueService queueService,
        IInventoryTransferQueueService transferQueueService,
        ISAPServiceLayerClient sapClient,
        IBatchInventoryValidationService batchValidation,
        ILogger<DesktopIntegrationController> logger)
    {
        _reservationService = reservationService;
        _queueService = queueService;
        _transferQueueService = transferQueueService;
        _sapClient = sapClient;
        _batchValidation = batchValidation;
        _logger = logger;
    }

    #region Stock Reservations

    /// <summary>
    /// Creates a stock reservation for a pending invoice from the desktop app.
    /// This reserves stock server-side to prevent negative quantities even before SAP posting.
    /// The reservation holds stock for a configurable duration (default 60 minutes).
    /// </summary>
    /// <param name="request">The reservation request with line items</param>
    /// <returns>The created reservation with allocated batches</returns>
    /// <remarks>
    /// Use this endpoint when a sale is committed in the desktop app but not yet posted to SAP.
    /// The reserved stock will be held until:
    /// - The reservation is confirmed (posted to SAP)
    /// - The reservation is cancelled
    /// - The reservation expires
    /// 
    /// Sample request:
    /// ```json
    /// {
    ///   "externalReferenceId": "DESKTOP-INV-2024-001",
    ///   "sourceSystem": "DESKTOP_APP",
    ///   "documentType": "Invoice",
    ///   "cardCode": "C001",
    ///   "cardName": "Customer Name",
    ///   "reservationDurationMinutes": 60,
    ///   "lines": [
    ///     {
    ///       "lineNum": 1,
    ///       "itemCode": "ITEM001",
    ///       "quantity": 10,
    ///       "warehouseCode": "WH01",
    ///       "unitPrice": 100.00,
    ///       "autoAllocateBatches": true
    ///     }
    ///   ]
    /// }
    /// ```
    /// </remarks>
    [HttpPost("reservations")]
    [ProducesResponseType(typeof(StockReservationResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(StockReservationResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(StockReservationResponseDto), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateReservation(
        [FromBody] CreateStockReservationRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new StockReservationResponseDto
            {
                Success = false,
                Message = "Validation failed",
                Errors = ModelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => new StockReservationErrorDto
                    {
                        ErrorCode = ReservationErrorCode.ValidationFailed,
                        Message = e.ErrorMessage
                    })
                    .ToList()
            });
        }

        var createdBy = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("client_id")?.Value;

        _logger.LogInformation(
            "Desktop app creating reservation: ExternalRef={ExternalRef}, Source={Source}, Lines={LineCount}",
            request.ExternalReferenceId, request.SourceSystem, request.Lines.Count);

        var result = await _reservationService.CreateReservationAsync(request, createdBy, cancellationToken);

        if (!result.Success)
        {
            // Check if it's a duplicate reference (conflict)
            if (result.Errors.Any(e => e.ErrorCode == ReservationErrorCode.DuplicateReference))
            {
                return Conflict(result);
            }

            return BadRequest(result);
        }

        return CreatedAtAction(
            nameof(GetReservation),
            new { reservationId = result.Reservation!.ReservationId },
            result);
    }

    /// <summary>
    /// Gets a reservation by its ID.
    /// </summary>
    /// <param name="reservationId">The reservation ID (GUID)</param>
    [HttpGet("reservations/{reservationId}")]
    [ProducesResponseType(typeof(StockReservationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReservation(
        string reservationId,
        CancellationToken cancellationToken)
    {
        var reservation = await _reservationService.GetReservationAsync(reservationId, cancellationToken);

        if (reservation == null)
        {
            return NotFound(new { message = $"Reservation '{reservationId}' not found" });
        }

        return Ok(reservation);
    }

    /// <summary>
    /// Gets a reservation by its external reference ID.
    /// </summary>
    /// <param name="externalReferenceId">The external reference ID from the source system</param>
    [HttpGet("reservations/by-reference/{externalReferenceId}")]
    [ProducesResponseType(typeof(StockReservationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReservationByReference(
        string externalReferenceId,
        CancellationToken cancellationToken)
    {
        var reservation = await _reservationService.GetReservationByExternalReferenceAsync(
            externalReferenceId, cancellationToken);

        if (reservation == null)
        {
            return NotFound(new { message = $"Reservation with external reference '{externalReferenceId}' not found" });
        }

        return Ok(reservation);
    }

    /// <summary>
    /// Lists reservations with filtering and pagination.
    /// </summary>
    /// <param name="sourceSystem">Filter by source system (e.g., "DESKTOP_APP")</param>
    /// <param name="status">Filter by status (Pending, Confirmed, Cancelled, Expired)</param>
    /// <param name="cardCode">Filter by customer code</param>
    /// <param name="activeOnly">Include only active (non-expired) reservations (default: true)</param>
    /// <param name="page">Page number (1-based)</param>
    /// <param name="pageSize">Page size (max 100)</param>
    [HttpGet("reservations")]
    [ProducesResponseType(typeof(ReservationListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListReservations(
        [FromQuery] string? sourceSystem = null,
        [FromQuery] string? status = null,
        [FromQuery] string? cardCode = null,
        [FromQuery] string? externalReferenceId = null,
        [FromQuery] bool activeOnly = true,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var query = new ReservationQueryParams
        {
            SourceSystem = sourceSystem,
            Status = status,
            CardCode = cardCode,
            ExternalReferenceId = externalReferenceId,
            ActiveOnly = activeOnly,
            Page = page,
            PageSize = Math.Min(pageSize, 100)
        };

        var result = await _reservationService.ListReservationsAsync(query, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Confirms a reservation by posting it to SAP as an invoice.
    /// This completes the sale and releases the reservation.
    /// </summary>
    /// <param name="request">The confirmation request</param>
    /// <remarks>
    /// Call this endpoint when the desktop app is ready to finalize the sale.
    /// The reservation will be converted to an SAP invoice and optionally fiscalized.
    /// 
    /// Sample request:
    /// ```json
    /// {
    ///   "reservationId": "abc12345-...",
    ///   "numAtCard": "Customer PO #123",
    ///   "comments": "Delivery note: Leave at reception",
    ///   "fiscalize": true
    /// }
    /// ```
    /// </remarks>
    [HttpPost("reservations/confirm")]
    [ProducesResponseType(typeof(ConfirmReservationResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ConfirmReservationResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ConfirmReservationResponseDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ConfirmReservation(
        [FromBody] ConfirmReservationRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new ConfirmReservationResponseDto
            {
                Success = false,
                Message = "Validation failed",
                Errors = ModelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => e.ErrorMessage)
                    .ToList()
            });
        }

        _logger.LogInformation("Desktop app confirming reservation: {ReservationId}", request.ReservationId);

        var result = await _reservationService.ConfirmReservationAsync(request, cancellationToken);

        if (!result.Success)
        {
            if (result.Errors.Any(e => e.Contains("not found", StringComparison.OrdinalIgnoreCase)))
            {
                return NotFound(result);
            }
            return BadRequest(result);
        }

        return Ok(result);
    }

    /// <summary>
    /// Cancels a reservation and releases the reserved stock.
    /// Use this when a sale is abandoned or needs to be recreated.
    /// </summary>
    /// <param name="request">The cancellation request</param>
    [HttpPost("reservations/cancel")]
    [ProducesResponseType(typeof(StockReservationResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(StockReservationResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(StockReservationResponseDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelReservation(
        [FromBody] CancelReservationRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new StockReservationResponseDto
            {
                Success = false,
                Message = "Validation failed"
            });
        }

        _logger.LogInformation("Desktop app cancelling reservation: {ReservationId}", request.ReservationId);

        var result = await _reservationService.CancelReservationAsync(request, cancellationToken);

        if (!result.Success)
        {
            if (result.Errors.Any(e => e.ErrorCode == ReservationErrorCode.ReservationNotFound))
            {
                return NotFound(result);
            }
            return BadRequest(result);
        }

        return Ok(result);
    }

    /// <summary>
    /// Renews/extends a reservation before it expires.
    /// Use this to keep a reservation active if the sale is still in progress.
    /// </summary>
    /// <param name="request">The renewal request</param>
    [HttpPost("reservations/renew")]
    [ProducesResponseType(typeof(StockReservationResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(StockReservationResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(StockReservationResponseDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RenewReservation(
        [FromBody] RenewReservationRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new StockReservationResponseDto
            {
                Success = false,
                Message = "Validation failed"
            });
        }

        _logger.LogInformation("Desktop app renewing reservation: {ReservationId} for {Minutes} minutes",
            request.ReservationId, request.ExtensionMinutes);

        var result = await _reservationService.RenewReservationAsync(request, cancellationToken);

        if (!result.Success)
        {
            if (result.Errors.Any(e => e.ErrorCode == ReservationErrorCode.ReservationNotFound))
            {
                return NotFound(result);
            }
            return BadRequest(result);
        }

        return Ok(result);
    }

    #endregion

    #region Stock Information

    /// <summary>
    /// Gets available stock for an item in a warehouse, accounting for existing reservations.
    /// This shows the ACTUAL available quantity that can be sold.
    /// </summary>
    /// <param name="itemCode">The item code</param>
    /// <param name="warehouseCode">The warehouse code</param>
    [HttpGet("stock/{warehouseCode}/{itemCode}")]
    [ProducesResponseType(typeof(ReservedStockSummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAvailableStock(
        string warehouseCode,
        string itemCode,
        CancellationToken cancellationToken)
    {
        var summary = await _reservationService.GetReservedStockSummaryAsync(
            itemCode, warehouseCode, cancellationToken);

        return Ok(summary);
    }

    /// <summary>
    /// Gets available stock for multiple items in a warehouse.
    /// Useful for loading stock levels for a product list.
    /// </summary>
    /// <param name="warehouseCode">The warehouse code</param>
    /// <param name="itemCodes">Comma-separated list of item codes</param>
    [HttpGet("stock/{warehouseCode}")]
    [ProducesResponseType(typeof(List<ReservedStockSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAvailableStockBulk(
        string warehouseCode,
        [FromQuery] string? itemCodes = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(itemCodes))
        {
            // Return all stock in warehouse
            var stockItems = await _sapClient.GetStockQuantitiesInWarehouseAsync(warehouseCode, cancellationToken);
            var summaries = new List<ReservedStockSummaryDto>();

            foreach (var item in stockItems.Take(100)) // Limit to 100 items
            {
                var summary = await _reservationService.GetReservedStockSummaryAsync(
                    item.ItemCode ?? "", warehouseCode, cancellationToken);
                summaries.Add(summary);
            }

            return Ok(summaries);
        }

        var codes = itemCodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var results = new List<ReservedStockSummaryDto>();

        foreach (var code in codes)
        {
            var summary = await _reservationService.GetReservedStockSummaryAsync(
                code, warehouseCode, cancellationToken);
            results.Add(summary);
        }

        return Ok(results);
    }

    /// <summary>
    /// Gets available batches for a batch-managed item, accounting for reservations.
    /// Returns batches sorted by FEFO (First Expiry First Out).
    /// </summary>
    /// <param name="itemCode">The item code</param>
    /// <param name="warehouseCode">The warehouse code</param>
    [HttpGet("stock/{warehouseCode}/{itemCode}/batches")]
    [ProducesResponseType(typeof(List<AvailableBatchWithReservationsDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAvailableBatches(
        string warehouseCode,
        string itemCode,
        CancellationToken cancellationToken)
    {
        var batches = await _batchValidation.GetAvailableBatchesAsync(
            itemCode, warehouseCode, BatchAllocationStrategy.FEFO, cancellationToken);

        var result = new List<AvailableBatchWithReservationsDto>();

        foreach (var batch in batches)
        {
            var reservedQty = await _reservationService.GetReservedBatchQuantityAsync(
                itemCode, warehouseCode, batch.BatchNumber ?? "", cancellationToken);

            result.Add(new AvailableBatchWithReservationsDto
            {
                BatchNumber = batch.BatchNumber ?? "",
                PhysicalQuantity = batch.AvailableQuantity,
                ReservedQuantity = reservedQty,
                AvailableQuantity = batch.AvailableQuantity - reservedQty,
                ExpiryDate = batch.ExpiryDate,
                ManufacturingDate = batch.AdmissionDate, // Using AdmissionDate as manufacturing date proxy
                Status = batch.IsRecommended ? "Recommended" : "Available"
            });
        }

        return Ok(result);
    }

    /// <summary>
    /// Validates if stock is available for a potential reservation without creating it.
    /// Use this for real-time validation as items are added to a sale.
    /// </summary>
    /// <param name="request">The lines to validate</param>
    [HttpPost("stock/validate")]
    [ProducesResponseType(typeof(StockValidationResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> ValidateStockAvailability(
        [FromBody] ValidateStockRequest request,
        CancellationToken cancellationToken)
    {
        var (isValid, errors) = await _reservationService.ValidateStockAvailabilityAsync(
            request.Lines, request.ExcludeReservationId, cancellationToken);

        return Ok(new StockValidationResultDto
        {
            IsValid = isValid,
            Errors = errors,
            Message = isValid ? "Stock is available" : "Some items have insufficient stock"
        });
    }

    #endregion

    #region Quick Invoice (Reserve + Confirm in one call)

    /// <summary>
    /// Creates an invoice directly without a separate reservation step.
    /// This is a convenience endpoint that reserves stock and immediately posts to SAP.
    /// Use this for quick sales where reservation is not needed.
    /// </summary>
    /// <param name="request">The invoice request</param>
    /// <remarks>
    /// This endpoint:
    /// 1. Validates stock availability (considering existing reservations)
    /// 2. Creates a temporary reservation
    /// 3. Posts the invoice to SAP
    /// 4. Marks the reservation as confirmed
    /// 
    /// If SAP posting fails, the reservation is automatically cancelled.
    /// </remarks>
    [HttpPost("invoices")]
    [ProducesResponseType(typeof(ConfirmReservationResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(StockReservationResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateInvoiceDirect(
        [FromBody] CreateDesktopInvoiceRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new StockReservationResponseDto
            {
                Success = false,
                Message = "Validation failed"
            });
        }

        var createdBy = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("client_id")?.Value;

        // Generate a unique reference if not provided
        var externalRef = request.ExternalReferenceId ?? $"DESKTOP-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString()[..8]}";

        _logger.LogInformation("Desktop app creating direct invoice: {ExternalRef}", externalRef);

        // Step 1: Create a reservation
        var reservationRequest = new CreateStockReservationRequest
        {
            ExternalReferenceId = externalRef,
            SourceSystem = request.SourceSystem ?? "DESKTOP_APP",
            DocumentType = ReservationDocumentType.Invoice,
            CardCode = request.CardCode,
            CardName = request.CardName,
            Currency = request.DocCurrency,
            ReservationDurationMinutes = 5, // Short duration for direct invoices
            Lines = request.Lines.Select(l => new CreateStockReservationLineRequest
            {
                LineNum = l.LineNum,
                ItemCode = l.ItemCode,
                ItemDescription = l.ItemDescription,
                Quantity = l.Quantity,
                UoMCode = l.UoMCode,
                WarehouseCode = l.WarehouseCode,
                UnitPrice = l.UnitPrice ?? 0,
                TaxCode = l.TaxCode,
                DiscountPercent = l.DiscountPercent ?? 0,
                BatchNumbers = l.BatchNumbers?.Select(b => new ReservationBatchRequest
                {
                    BatchNumber = b.BatchNumber,
                    Quantity = b.Quantity
                }).ToList(),
                AutoAllocateBatches = l.AutoAllocateBatches
            }).ToList()
        };

        var reservationResult = await _reservationService.CreateReservationAsync(
            reservationRequest, createdBy, cancellationToken);

        if (!reservationResult.Success)
        {
            return BadRequest(reservationResult);
        }

        // Step 2: Immediately confirm the reservation
        var confirmRequest = new ConfirmReservationRequest
        {
            ReservationId = reservationResult.Reservation!.ReservationId,
            DocDate = request.DocDate,
            DocDueDate = request.DocDueDate,
            NumAtCard = request.NumAtCard,
            Comments = request.Comments,
            SalesPersonCode = request.SalesPersonCode,
            Fiscalize = request.Fiscalize
        };

        var confirmResult = await _reservationService.ConfirmReservationAsync(confirmRequest, cancellationToken);

        if (!confirmResult.Success)
        {
            // Cancel the reservation if SAP posting failed
            await _reservationService.CancelReservationAsync(new CancelReservationRequest
            {
                ReservationId = reservationResult.Reservation.ReservationId,
                Reason = $"SAP posting failed: {string.Join(", ", confirmResult.Errors)}"
            }, cancellationToken);

            return BadRequest(confirmResult);
        }

        return CreatedAtAction(
            nameof(GetReservation),
            new { reservationId = reservationResult.Reservation.ReservationId },
            confirmResult);
    }

    #endregion

    #region Queued Invoice (Reserve + Queue for batch posting)

    /// <summary>
    /// Creates a queued invoice - reserves stock and queues for batch posting to SAP.
    /// This is the RECOMMENDED approach for desktop apps to avoid concurrency issues.
    /// </summary>
    /// <param name="request">The invoice request</param>
    /// <remarks>
    /// This endpoint:
    /// 1. Validates stock availability (considering existing reservations)
    /// 2. Creates a reservation to hold the stock
    /// 3. Queues the invoice for batch posting to SAP (processed every 10 seconds)
    /// 4. Returns immediately with reservation ID and queue status
    /// 
    /// The desktop app should:
    /// 1. Call this endpoint when a sale is confirmed
    /// 2. Poll GET /queue/{externalReference} to check if SAP posting is complete
    /// 3. Display the SAP DocNum once status is "Completed"
    /// 
    /// Benefits:
    /// - No race conditions - stock is reserved immediately
    /// - No timeout issues - background service handles SAP posting
    /// - Automatic retry on transient failures
    /// - Works even if SAP is temporarily unavailable
    /// </remarks>
    [HttpPost("invoices/queued")]
    [ProducesResponseType(typeof(QueuedInvoiceResponseDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(StockReservationResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateQueuedInvoice(
        [FromBody] CreateDesktopInvoiceRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new StockReservationResponseDto
            {
                Success = false,
                Message = "Validation failed"
            });
        }

        var createdBy = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("client_id")?.Value;

        // Generate a unique reference if not provided
        var externalRef = request.ExternalReferenceId ??
            $"DESKTOP-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString()[..8]}";

        _logger.LogInformation("Desktop app creating queued invoice: {ExternalRef}", externalRef);

        // Step 1: Create a reservation to hold the stock
        var reservationRequest = new CreateStockReservationRequest
        {
            ExternalReference = externalRef,
            ExternalReferenceId = externalRef,
            SourceSystem = request.SourceSystem ?? "DESKTOP_APP",
            DocumentType = ReservationDocumentType.Invoice,
            CardCode = request.CardCode,
            CardName = request.CardName,
            Currency = request.DocCurrency,
            ReservationDurationMinutes = 60, // Hold stock for 1 hour (background service will process within seconds)
            RequiresFiscalization = request.Fiscalize,
            Notes = request.Comments,
            Lines = request.Lines.Select(l => new CreateStockReservationLineRequest
            {
                LineNum = l.LineNum,
                ItemCode = l.ItemCode,
                ItemDescription = l.ItemDescription,
                Quantity = l.Quantity,
                UoMCode = l.UoMCode,
                WarehouseCode = l.WarehouseCode,
                UnitPrice = l.UnitPrice ?? 0,
                TaxCode = l.TaxCode,
                DiscountPercent = l.DiscountPercent ?? 0,
                BatchNumbers = l.BatchNumbers?.Select(b => new ReservationBatchRequest
                {
                    BatchNumber = b.BatchNumber,
                    Quantity = b.Quantity
                }).ToList(),
                AutoAllocateBatches = l.AutoAllocateBatches
            }).ToList()
        };

        var reservationResult = await _reservationService.CreateReservationAsync(
            reservationRequest, createdBy, cancellationToken);

        if (!reservationResult.Success)
        {
            return BadRequest(reservationResult);
        }

        // Step 2: Queue the invoice for batch posting
        var queueResult = await _queueService.EnqueueInvoiceAsync(
            reservationRequest,
            reservationResult.Reservation!.ReservationId,
            createdBy,
            cancellationToken);

        if (!queueResult.Success)
        {
            // If queuing fails, cancel the reservation
            await _reservationService.CancelReservationAsync(new CancelReservationRequest
            {
                ReservationId = reservationResult.Reservation.ReservationId,
                Reason = $"Failed to queue invoice: {queueResult.ErrorMessage}"
            }, cancellationToken);

            return BadRequest(new StockReservationResponseDto
            {
                Success = false,
                Message = queueResult.ErrorMessage ?? "Failed to queue invoice"
            });
        }

        _logger.LogInformation(
            "Invoice queued successfully: ExternalRef={ExternalRef}, ReservationId={ReservationId}, QueueId={QueueId}",
            externalRef, reservationResult.Reservation.ReservationId, queueResult.QueueId);

        return Accepted(new QueuedInvoiceResponseDto
        {
            Success = true,
            Message = "Invoice queued for processing. Poll the status endpoint to check completion.",
            ExternalReference = externalRef,
            ReservationId = reservationResult.Reservation.ReservationId,
            QueueId = queueResult.QueueId,
            Status = "Pending",
            EstimatedProcessingSeconds = 15,
            StatusUrl = Url.Action(nameof(GetQueueStatus), new { externalReference = externalRef })
        });
    }

    /// <summary>
    /// Gets the status of a queued invoice by external reference.
    /// Use this to poll for completion after creating a queued invoice.
    /// </summary>
    /// <param name="externalReference">The external reference ID</param>
    [HttpGet("queue/{externalReference}")]
    [ProducesResponseType(typeof(InvoiceQueueStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetQueueStatus(
        string externalReference,
        CancellationToken cancellationToken)
    {
        var status = await _queueService.GetQueueStatusAsync(externalReference, cancellationToken);

        if (status == null)
        {
            return NotFound(new { message = $"No queued invoice found with reference '{externalReference}'" });
        }

        return Ok(status);
    }

    /// <summary>
    /// Gets the status of a queued invoice by reservation ID.
    /// </summary>
    /// <param name="reservationId">The reservation ID</param>
    [HttpGet("queue/by-reservation/{reservationId}")]
    [ProducesResponseType(typeof(InvoiceQueueStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetQueueStatusByReservation(
        string reservationId,
        CancellationToken cancellationToken)
    {
        var status = await _queueService.GetQueueStatusByReservationAsync(reservationId, cancellationToken);

        if (status == null)
        {
            return NotFound(new { message = $"No queued invoice found for reservation '{reservationId}'" });
        }

        return Ok(status);
    }

    /// <summary>
    /// Gets all pending invoices in the queue.
    /// </summary>
    /// <param name="sourceSystem">Optional filter by source system</param>
    /// <param name="limit">Maximum results (default 100)</param>
    [HttpGet("queue")]
    [ProducesResponseType(typeof(List<InvoiceQueueStatusDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPendingQueue(
        [FromQuery] string? sourceSystem = null,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var pending = await _queueService.GetPendingInvoicesAsync(sourceSystem, limit, cancellationToken);
        return Ok(pending);
    }

    /// <summary>
    /// Gets invoices that require manual review due to repeated failures.
    /// </summary>
    [HttpGet("queue/review")]
    [ProducesResponseType(typeof(List<InvoiceQueueStatusDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetInvoicesRequiringReview(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var invoices = await _queueService.GetInvoicesRequiringReviewAsync(limit, cancellationToken);
        return Ok(invoices);
    }

    /// <summary>
    /// Gets queue statistics.
    /// </summary>
    [HttpGet("queue/stats")]
    [ProducesResponseType(typeof(InvoiceQueueStatsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetQueueStats(CancellationToken cancellationToken)
    {
        var stats = await _queueService.GetQueueStatsAsync(cancellationToken);
        return Ok(stats);
    }

    /// <summary>
    /// Cancels a queued invoice (only if still pending).
    /// This also releases the associated stock reservation.
    /// </summary>
    /// <param name="externalReference">The external reference ID</param>
    [HttpDelete("queue/{externalReference}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelQueuedInvoice(
        string externalReference,
        CancellationToken cancellationToken)
    {
        var status = await _queueService.GetQueueStatusAsync(externalReference, cancellationToken);

        if (status == null)
        {
            return NotFound(new { message = $"No queued invoice found with reference '{externalReference}'" });
        }

        if (!status.CanCancel)
        {
            return BadRequest(new { message = $"Invoice cannot be cancelled - current status: {status.Status}" });
        }

        var cancelledBy = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var success = await _queueService.CancelQueuedInvoiceAsync(externalReference, cancelledBy, cancellationToken);

        if (!success)
        {
            return BadRequest(new { message = "Failed to cancel queued invoice" });
        }

        // Also cancel the reservation
        await _reservationService.CancelReservationAsync(new CancelReservationRequest
        {
            ReservationId = status.ReservationId,
            Reason = "Queued invoice cancelled"
        }, cancellationToken);

        return NoContent();
    }

    /// <summary>
    /// Retries a failed invoice.
    /// </summary>
    /// <param name="externalReference">The external reference ID</param>
    [HttpPost("queue/{externalReference}/retry")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RetryQueuedInvoice(
        string externalReference,
        CancellationToken cancellationToken)
    {
        var status = await _queueService.GetQueueStatusAsync(externalReference, cancellationToken);

        if (status == null)
        {
            return NotFound(new { message = $"No queued invoice found with reference '{externalReference}'" });
        }

        if (!status.CanRetry && !status.IsFailed)
        {
            return BadRequest(new { message = $"Invoice cannot be retried - current status: {status.Status}" });
        }

        var success = await _queueService.RetryInvoiceAsync(externalReference, cancellationToken);

        if (!success)
        {
            return BadRequest(new { message = "Failed to retry invoice" });
        }

        return Ok(new { message = "Invoice will be retried shortly", status = "Pending" });
    }

    #endregion

    #region Queued Inventory Transfers

    /// <summary>
    /// Creates a queued inventory transfer - queues for batch posting to SAP.
    /// This is the RECOMMENDED approach for desktop apps to handle inventory transfers.
    /// </summary>
    /// <param name="request">The transfer request</param>
    /// <remarks>
    /// This endpoint:
    /// 1. Validates the transfer request
    /// 2. Queues the transfer for batch posting to SAP (processed every 10 seconds)
    /// 3. Returns immediately with queue status
    /// 
    /// The desktop app should:
    /// 1. Call this endpoint when a transfer is confirmed
    /// 2. Poll GET /transfer-queue/{externalReference} to check if SAP posting is complete
    /// 3. Display the SAP DocNum once status is "Completed"
    /// </remarks>
    [HttpPost("transfers/queued")]
    [ProducesResponseType(typeof(QueuedTransferResponseDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateQueuedTransfer(
        [FromBody] CreateDesktopTransferRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new
            {
                Success = false,
                Message = "Validation failed",
                Errors = ModelState.Values.SelectMany(v => v.Errors.Select(e => e.ErrorMessage))
            });
        }

        var createdBy = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ??
            User.FindFirst("client_id")?.Value;

        _logger.LogInformation("Desktop app creating queued inventory transfer: From={From}, To={To}",
            request.FromWarehouse, request.ToWarehouse);

        var queueResult = await _transferQueueService.EnqueueTransferAsync(
            request,
            null, // No reservation for transfers currently
            createdBy,
            cancellationToken);

        if (!queueResult.Success)
        {
            return BadRequest(new
            {
                Success = false,
                Message = queueResult.ErrorMessage ?? "Failed to queue transfer"
            });
        }

        _logger.LogInformation(
            "Inventory transfer queued successfully: ExternalRef={ExternalRef}, QueueId={QueueId}",
            queueResult.ExternalReference, queueResult.QueueId);

        return Accepted(new QueuedTransferResponseDto
        {
            Success = true,
            Message = "Inventory transfer queued for processing. Poll the status endpoint to check completion.",
            ExternalReference = queueResult.ExternalReference,
            QueueId = queueResult.QueueId,
            Status = "Pending",
            EstimatedProcessingSeconds = 15,
            StatusUrl = Url.Action(nameof(GetTransferQueueStatus), new { externalReference = queueResult.ExternalReference })
        });
    }

    /// <summary>
    /// Gets the status of a queued inventory transfer by external reference.
    /// </summary>
    /// <param name="externalReference">The external reference ID</param>
    [HttpGet("transfer-queue/{externalReference}")]
    [ProducesResponseType(typeof(InventoryTransferQueueStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTransferQueueStatus(
        string externalReference,
        CancellationToken cancellationToken)
    {
        var status = await _transferQueueService.GetQueueStatusAsync(externalReference, cancellationToken);

        if (status == null)
        {
            return NotFound(new { message = $"No queued transfer found with reference '{externalReference}'" });
        }

        return Ok(status);
    }

    /// <summary>
    /// Gets all pending inventory transfers in the queue.
    /// </summary>
    /// <param name="sourceSystem">Optional filter by source system</param>
    /// <param name="limit">Maximum results (default 100)</param>
    [HttpGet("transfer-queue")]
    [ProducesResponseType(typeof(List<InventoryTransferQueueStatusDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPendingTransferQueue(
        [FromQuery] string? sourceSystem = null,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var pending = await _transferQueueService.GetPendingTransfersAsync(sourceSystem, limit, cancellationToken);
        return Ok(pending);
    }

    /// <summary>
    /// Gets inventory transfers that require manual review due to repeated failures.
    /// </summary>
    [HttpGet("transfer-queue/review")]
    [ProducesResponseType(typeof(List<InventoryTransferQueueStatusDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTransfersRequiringReview(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var transfers = await _transferQueueService.GetTransfersRequiringReviewAsync(limit, cancellationToken);
        return Ok(transfers);
    }

    /// <summary>
    /// Gets queue statistics for inventory transfers.
    /// </summary>
    [HttpGet("transfer-queue/stats")]
    [ProducesResponseType(typeof(InventoryTransferQueueStatsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTransferQueueStats(CancellationToken cancellationToken)
    {
        var stats = await _transferQueueService.GetQueueStatsAsync(cancellationToken);
        return Ok(stats);
    }

    /// <summary>
    /// Cancels a queued inventory transfer (only if still pending).
    /// </summary>
    /// <param name="externalReference">The external reference ID</param>
    [HttpDelete("transfer-queue/{externalReference}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelQueuedTransfer(
        string externalReference,
        CancellationToken cancellationToken)
    {
        var status = await _transferQueueService.GetQueueStatusAsync(externalReference, cancellationToken);

        if (status == null)
        {
            return NotFound(new { message = $"No queued transfer found with reference '{externalReference}'" });
        }

        if (!status.CanCancel)
        {
            return BadRequest(new { message = $"Transfer cannot be cancelled - current status: {status.Status}" });
        }

        var cancelledBy = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var success = await _transferQueueService.CancelQueuedTransferAsync(externalReference, cancelledBy, cancellationToken);

        if (!success)
        {
            return BadRequest(new { message = "Failed to cancel queued transfer" });
        }

        return NoContent();
    }

    /// <summary>
    /// Retries a failed inventory transfer.
    /// </summary>
    /// <param name="externalReference">The external reference ID</param>
    [HttpPost("transfer-queue/{externalReference}/retry")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RetryQueuedTransfer(
        string externalReference,
        CancellationToken cancellationToken)
    {
        var status = await _transferQueueService.GetQueueStatusAsync(externalReference, cancellationToken);

        if (status == null)
        {
            return NotFound(new { message = $"No queued transfer found with reference '{externalReference}'" });
        }

        if (!status.CanRetry && !status.IsFailed)
        {
            return BadRequest(new { message = $"Transfer cannot be retried - current status: {status.Status}" });
        }

        var success = await _transferQueueService.RetryTransferAsync(externalReference, cancellationToken);

        if (!success)
        {
            return BadRequest(new { message = "Failed to retry transfer" });
        }

        return Ok(new { message = "Transfer will be retried shortly", status = "Pending" });
    }

    #endregion
}

#region Additional DTOs for Desktop Integration

/// <summary>
/// Response for queued invoice creation
/// </summary>
public class QueuedInvoiceResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string ExternalReference { get; set; } = string.Empty;
    public string ReservationId { get; set; } = string.Empty;
    public int QueueId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int EstimatedProcessingSeconds { get; set; }
    public string? StatusUrl { get; set; }
}

/// <summary>
/// Response for queued inventory transfer creation
/// </summary>
public class QueuedTransferResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string ExternalReference { get; set; } = string.Empty;
    public int QueueId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int EstimatedProcessingSeconds { get; set; }
    public string? StatusUrl { get; set; }
}

/// <summary>
/// Batch information including reservation data
/// </summary>
public class AvailableBatchWithReservationsDto
{
    public string BatchNumber { get; set; } = string.Empty;
    public decimal PhysicalQuantity { get; set; }
    public decimal ReservedQuantity { get; set; }
    public decimal AvailableQuantity { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public DateTime? ManufacturingDate { get; set; }
    public string? Status { get; set; }
}

/// <summary>
/// Request to validate stock availability
/// </summary>
public class ValidateStockRequest
{
    public List<CreateStockReservationLineRequest> Lines { get; set; } = new();
    public string? ExcludeReservationId { get; set; }
}

/// <summary>
/// Result of stock validation
/// </summary>
public class StockValidationResultDto
{
    public bool IsValid { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<StockReservationErrorDto> Errors { get; set; } = new();
}

/// <summary>
/// Request to create a direct invoice from desktop app
/// </summary>
public class CreateDesktopInvoiceRequest
{
    public string? ExternalReferenceId { get; set; }
    public string? SourceSystem { get; set; }

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Customer code is required")]
    public string CardCode { get; set; } = string.Empty;

    public string? CardName { get; set; }
    public string? DocDate { get; set; }
    public string? DocDueDate { get; set; }
    public string? NumAtCard { get; set; }
    public string? Comments { get; set; }
    public string? DocCurrency { get; set; }
    public int? SalesPersonCode { get; set; }
    public bool Fiscalize { get; set; } = true;

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "At least one line item is required")]
    [System.ComponentModel.DataAnnotations.MinLength(1)]
    public List<CreateDesktopInvoiceLineRequest> Lines { get; set; } = new();
}

/// <summary>
/// Line item for desktop invoice
/// </summary>
public class CreateDesktopInvoiceLineRequest
{
    public int LineNum { get; set; }

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Item code is required")]
    public string ItemCode { get; set; } = string.Empty;

    public string? ItemDescription { get; set; }

    [System.ComponentModel.DataAnnotations.Range(0.000001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero")]
    public decimal Quantity { get; set; }

    public decimal? UnitPrice { get; set; }

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Warehouse code is required")]
    public string WarehouseCode { get; set; } = string.Empty;

    public string? TaxCode { get; set; }
    public decimal? DiscountPercent { get; set; }
    public string? UoMCode { get; set; }
    public bool AutoAllocateBatches { get; set; } = true;
    public List<DesktopBatchRequest>? BatchNumbers { get; set; }
}

/// <summary>
/// Batch allocation for desktop invoice line
/// </summary>
public class DesktopBatchRequest
{
    [System.ComponentModel.DataAnnotations.Required]
    public string BatchNumber { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Range(0.000001, double.MaxValue)]
    public decimal Quantity { get; set; }
}

#endregion
