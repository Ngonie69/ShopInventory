using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
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
    private readonly IStockValidationService _stockValidation;
    private readonly IInvoicePdfService _invoicePdfService;
    private readonly ISalesOrderService _salesOrderService;
    private readonly SAPSettings _sapSettings;
    private readonly ILogger<DesktopIntegrationController> _logger;

    public DesktopIntegrationController(
        IStockReservationService reservationService,
        IInvoiceQueueService queueService,
        IInventoryTransferQueueService transferQueueService,
        ISAPServiceLayerClient sapClient,
        IBatchInventoryValidationService batchValidation,
        IStockValidationService stockValidation,
        IInvoicePdfService invoicePdfService,
        ISalesOrderService salesOrderService,
        IOptions<SAPSettings> sapSettings,
        ILogger<DesktopIntegrationController> logger)
    {
        _reservationService = reservationService;
        _queueService = queueService;
        _transferQueueService = transferQueueService;
        _sapClient = sapClient;
        _batchValidation = batchValidation;
        _stockValidation = stockValidation;
        _invoicePdfService = invoicePdfService;
        _salesOrderService = salesOrderService;
        _sapSettings = sapSettings.Value;
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

    #region Invoice Retrieval

    /// <summary>
    /// Gets an invoice from SAP by its DocEntry.
    /// </summary>
    /// <param name="docEntry">The SAP DocEntry</param>
    [HttpGet("invoices/{docEntry:int}")]
    [ProducesResponseType(typeof(InvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetInvoice(int docEntry, CancellationToken cancellationToken)
    {
        if (!_sapSettings.Enabled)
            return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });

        var invoice = await _sapClient.GetInvoiceByDocEntryAsync(docEntry, cancellationToken);
        if (invoice == null)
            return NotFound(new ErrorResponseDto { Message = $"Invoice with DocEntry {docEntry} not found" });

        return Ok(invoice.ToDto());
    }

    /// <summary>
    /// Gets an invoice from SAP by its DocNum.
    /// </summary>
    /// <param name="docNum">The SAP document number</param>
    [HttpGet("invoices/by-docnum/{docNum:int}")]
    [ProducesResponseType(typeof(InvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetInvoiceByDocNum(int docNum, CancellationToken cancellationToken)
    {
        if (!_sapSettings.Enabled)
            return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });

        var invoice = await _sapClient.GetInvoiceByDocNumAsync(docNum, cancellationToken);
        if (invoice == null)
            return NotFound(new ErrorResponseDto { Message = $"Invoice with DocNum {docNum} not found" });

        return Ok(invoice.ToDto());
    }

    /// <summary>
    /// Gets invoices for a specific customer, optionally filtered by date range.
    /// </summary>
    /// <param name="cardCode">The SAP business partner code</param>
    /// <param name="fromDate">Optional start date (yyyy-MM-dd)</param>
    /// <param name="toDate">Optional end date (yyyy-MM-dd)</param>
    [HttpGet("invoices/customer/{cardCode}")]
    [ProducesResponseType(typeof(List<InvoiceDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetInvoicesByCustomer(
        string cardCode,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        if (!_sapSettings.Enabled)
            return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });

        List<Models.Invoice> invoices;
        if (fromDate.HasValue && toDate.HasValue)
        {
            invoices = await _sapClient.GetInvoicesByCustomerAsync(
                cardCode, fromDate.Value, toDate.Value, cancellationToken);
        }
        else
        {
            invoices = await _sapClient.GetInvoicesByCustomerAsync(cardCode, cancellationToken);
        }

        return Ok(invoices.ToDto());
    }

    /// <summary>
    /// Gets invoices within a date range.
    /// </summary>
    /// <param name="fromDate">Start date (yyyy-MM-dd)</param>
    /// <param name="toDate">End date (yyyy-MM-dd)</param>
    [HttpGet("invoices/date-range")]
    [ProducesResponseType(typeof(List<InvoiceDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetInvoicesByDateRange(
        [FromQuery] DateTime fromDate,
        [FromQuery] DateTime toDate,
        CancellationToken cancellationToken = default)
    {
        if (!_sapSettings.Enabled)
            return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });

        if (fromDate > toDate)
            return BadRequest(new ErrorResponseDto { Message = "fromDate must be before or equal to toDate" });

        var invoices = await _sapClient.GetInvoicesByDateRangeAsync(fromDate, toDate, cancellationToken);
        return Ok(invoices.ToDto());
    }

    /// <summary>
    /// Gets invoices with pagination.
    /// </summary>
    /// <param name="page">Page number (1-based, default 1)</param>
    /// <param name="pageSize">Page size (max 100, default 20)</param>
    [HttpGet("invoices/paged")]
    [ProducesResponseType(typeof(List<InvoiceDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetPagedInvoices(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (!_sapSettings.Enabled)
            return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var invoices = await _sapClient.GetPagedInvoicesAsync(page, pageSize, cancellationToken);
        return Ok(invoices.ToDto());
    }

    /// <summary>
    /// Downloads an invoice as a PDF document.
    /// Includes business partner details (VAT, TIN, phone, email).
    /// </summary>
    /// <param name="docEntry">The SAP DocEntry</param>
    [HttpGet("invoices/{docEntry:int}/pdf")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> DownloadInvoicePdf(int docEntry, CancellationToken cancellationToken)
    {
        if (!_sapSettings.Enabled)
            return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });

        var invoice = await _sapClient.GetInvoiceByDocEntryAsync(docEntry, cancellationToken);
        if (invoice == null)
            return NotFound(new ErrorResponseDto { Message = $"Invoice with DocEntry {docEntry} not found" });

        var invoiceDto = invoice.ToDto();

        // Enrich with business partner details
        if (!string.IsNullOrEmpty(invoice.CardCode))
        {
            try
            {
                var bp = await _sapClient.GetBusinessPartnerByCodeAsync(invoice.CardCode, cancellationToken);
                if (bp != null)
                {
                    invoiceDto.CustomerVatNo = bp.VatRegNo;
                    invoiceDto.CustomerTinNumber = bp.TinNumber;
                    invoiceDto.CustomerPhone = bp.Phone1;
                    invoiceDto.CustomerEmail = bp.Email;
                }
            }
            catch (Exception bpEx)
            {
                _logger.LogWarning(bpEx, "Could not fetch business partner {CardCode} for PDF enrichment", invoice.CardCode);
            }
        }

        var pdfBytes = await _invoicePdfService.GenerateInvoicePdfAsync(invoiceDto);
        var fileName = $"Invoice_{invoiceDto.DocNum}_{DateTime.Now:yyyyMMdd}.pdf";
        return File(pdfBytes, "application/pdf", fileName);
    }

    /// <summary>
    /// Validates invoice lines for stock availability and batch allocation without creating.
    /// Use this for real-time validation before submitting an invoice.
    /// </summary>
    /// <param name="request">The invoice to validate</param>
    /// <param name="autoAllocateBatches">Whether to auto-allocate batches (default: true)</param>
    /// <param name="allocationStrategy">Batch allocation strategy: FEFO or FIFO (default: FEFO)</param>
    [HttpPost("invoices/validate")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BatchStockValidationResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ValidateInvoice(
        [FromBody] CreateDesktopInvoiceRequest request,
        [FromQuery] bool autoAllocateBatches = true,
        [FromQuery] BatchAllocationStrategy allocationStrategy = BatchAllocationStrategy.FEFO,
        CancellationToken cancellationToken = default)
    {
        if (!_sapSettings.Enabled)
            return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });

        if (!ModelState.IsValid)
        {
            return BadRequest(new ErrorResponseDto
            {
                Message = "Validation failed",
                Errors = ModelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => e.ErrorMessage)
                    .ToList()
            });
        }

        // Convert to CreateInvoiceRequest for batch validation
        var invoiceRequest = new Models.CreateInvoiceRequest
        {
            CardCode = request.CardCode,
            DocDate = request.DocDate,
            DocDueDate = request.DocDueDate,
            NumAtCard = request.NumAtCard,
            Comments = request.Comments,
            DocCurrency = request.DocCurrency,
            SalesPersonCode = request.SalesPersonCode,
            Lines = request.Lines.Select(l => new Models.CreateInvoiceLineRequest
            {
                ItemCode = l.ItemCode,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice ?? 0,
                WarehouseCode = l.WarehouseCode,
                TaxCode = l.TaxCode,
                DiscountPercent = l.DiscountPercent ?? 0,
                UoMCode = l.UoMCode,
                BatchNumbers = l.BatchNumbers?.Select(b => new Models.BatchNumberRequest
                {
                    BatchNumber = b.BatchNumber,
                    Quantity = b.Quantity
                }).ToList()
            }).ToList()
        };

        var result = await _batchValidation.ValidateAndAllocateBatchesAsync(
            invoiceRequest, autoAllocateBatches, allocationStrategy, cancellationToken);

        if (result.IsValid)
        {
            return Ok(new
            {
                isValid = true,
                message = "Invoice validation successful",
                strategy = allocationStrategy.ToString(),
                linesValidated = result.TotalLinesValidated,
                batchesAllocated = result.AllocatedLines.Sum(l => l.Batches.Count),
                allocatedLines = result.AllocatedLines,
                warnings = result.Warnings
            });
        }

        return BadRequest(new BatchStockValidationResponseDto
        {
            Message = "Invoice validation failed",
            IsValid = false,
            Errors = result.ValidationErrors,
            Warnings = result.Warnings,
            Suggestions = result.Suggestions
        });
    }

    #endregion

    #region Sales Order to Invoice Conversion

    /// <summary>
    /// Converts a sales order to a queued invoice, with optional modifications to lines.
    /// This allows the desktop app to adjust quantities, prices, remove lines, or add new lines
    /// during conversion — catering for changes between order and actual delivery.
    /// </summary>
    /// <param name="request">The conversion request with optional line overrides</param>
    /// <remarks>
    /// This endpoint:
    /// 1. Fetches the approved sales order from the local database
    /// 2. Uses the provided lines (or original order lines if none provided)
    /// 3. Reserves stock for the invoice lines
    /// 4. Queues the invoice for batch posting to SAP
    /// 5. Updates the sales order status to Fulfilled
    /// 
    /// If no lines are provided in the request, the original sales order lines are used as-is.
    /// If lines are provided, they REPLACE the original lines entirely — this supports:
    /// - Partial deliveries (reduce quantities)
    /// - Price adjustments
    /// - Removing items no longer available
    /// - Adding new items not on the original order
    /// 
    /// The desktop app should poll GET /queue/{externalReference} to check SAP posting status.
    /// 
    /// Sample request (with changes):
    /// ```json
    /// {
    ///   "salesOrderId": 42,
    ///   "numAtCard": "PO-2026-001",
    ///   "comments": "Partial delivery - Item B out of stock",
    ///   "lines": [
    ///     { "lineNum": 0, "itemCode": "ITEM-A", "quantity": 5, "unitPrice": 10.00, "warehouseCode": "WH01" },
    ///     { "lineNum": 1, "itemCode": "ITEM-C", "quantity": 2, "unitPrice": 25.00, "warehouseCode": "WH01" }
    ///   ]
    /// }
    /// ```
    /// </remarks>
    [HttpPost("sales-orders/convert-to-invoice")]
    [ProducesResponseType(typeof(ConvertSalesOrderToInvoiceResponseDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ConvertSalesOrderToInvoiceResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ConvertSalesOrderToInvoiceResponseDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ConvertSalesOrderToInvoice(
        [FromBody] ConvertSalesOrderToInvoiceRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new ConvertSalesOrderToInvoiceResponseDto
            {
                Success = false,
                Message = "Validation failed",
                Errors = ModelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => e.ErrorMessage)
                    .ToList()
            });
        }

        // Step 1: Fetch the sales order
        var order = await _salesOrderService.GetByIdFromLocalAsync(request.SalesOrderId, cancellationToken);

        if (order == null)
        {
            return NotFound(new ConvertSalesOrderToInvoiceResponseDto
            {
                Success = false,
                SalesOrderId = request.SalesOrderId,
                Message = $"Sales order with ID {request.SalesOrderId} not found"
            });
        }

        if (order.Status != Models.Entities.SalesOrderStatus.Approved)
        {
            return BadRequest(new ConvertSalesOrderToInvoiceResponseDto
            {
                Success = false,
                SalesOrderId = order.Id,
                SalesOrderNumber = order.OrderNumber,
                Message = $"Only approved orders can be converted to invoices. Current status: {order.StatusName}"
            });
        }

        if (!order.Lines.Any())
        {
            return BadRequest(new ConvertSalesOrderToInvoiceResponseDto
            {
                Success = false,
                SalesOrderId = order.Id,
                SalesOrderNumber = order.OrderNumber,
                Message = "Sales order has no line items"
            });
        }

        var createdBy = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("client_id")?.Value;
        var externalRef = request.ExternalReferenceId ??
            $"SO-CONV-{order.OrderNumber}-{Guid.NewGuid().ToString()[..8]}";

        _logger.LogInformation(
            "Converting sales order {OrderNumber} (ID: {OrderId}) to invoice: {ExternalRef}",
            order.OrderNumber, order.Id, externalRef);

        // Step 2: Build invoice lines - use provided lines or map from order
        List<CreateDesktopInvoiceLineRequest> invoiceLines;

        if (request.Lines != null && request.Lines.Any())
        {
            // Caller provided modified lines — use as-is
            invoiceLines = request.Lines;
            _logger.LogInformation(
                "Using {Count} custom lines for conversion (original order had {OriginalCount} lines)",
                invoiceLines.Count, order.Lines.Count);
        }
        else
        {
            // Map directly from order lines
            invoiceLines = order.Lines.Select((line, idx) => new CreateDesktopInvoiceLineRequest
            {
                LineNum = idx,
                ItemCode = line.ItemCode,
                ItemDescription = line.ItemDescription,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                WarehouseCode = line.WarehouseCode ?? order.WarehouseCode ?? "",
                UoMCode = line.UoMCode,
                DiscountPercent = line.DiscountPercent,
                AutoAllocateBatches = true
            }).ToList();
        }

        if (!invoiceLines.Any())
        {
            return BadRequest(new ConvertSalesOrderToInvoiceResponseDto
            {
                Success = false,
                SalesOrderId = order.Id,
                SalesOrderNumber = order.OrderNumber,
                Message = "No invoice lines to process"
            });
        }

        // Step 3: Create stock reservation
        var reservationRequest = new CreateStockReservationRequest
        {
            ExternalReference = externalRef,
            ExternalReferenceId = externalRef,
            SourceSystem = request.SourceSystem ?? "DESKTOP_APP",
            DocumentType = ReservationDocumentType.Invoice,
            CardCode = order.CardCode,
            CardName = order.CardName,
            Currency = request.DocCurrency ?? order.Currency,
            ReservationDurationMinutes = 60,
            RequiresFiscalization = request.Fiscalize,
            Notes = request.Comments ?? $"Converted from Sales Order {order.OrderNumber}",
            Lines = invoiceLines.Select(l => new CreateStockReservationLineRequest
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
            _logger.LogWarning(
                "Stock reservation failed for sales order {OrderNumber} conversion: {Errors}",
                order.OrderNumber,
                string.Join("; ", reservationResult.Errors?.Select(e => e.Message) ?? Array.Empty<string>()));

            return BadRequest(new ConvertSalesOrderToInvoiceResponseDto
            {
                Success = false,
                SalesOrderId = order.Id,
                SalesOrderNumber = order.OrderNumber,
                ExternalReference = externalRef,
                Message = "Stock reservation failed — insufficient stock or batch allocation error",
                Errors = reservationResult.Errors?.Select(e => e.Message).ToList() ?? new()
            });
        }

        // Step 4: Queue the invoice for batch posting to SAP
        var queueResult = await _queueService.EnqueueInvoiceAsync(
            reservationRequest,
            reservationResult.Reservation!.ReservationId,
            createdBy,
            cancellationToken);

        if (!queueResult.Success)
        {
            // Cancel the reservation if queuing fails
            await _reservationService.CancelReservationAsync(new CancelReservationRequest
            {
                ReservationId = reservationResult.Reservation.ReservationId,
                Reason = $"Failed to queue invoice from SO conversion: {queueResult.ErrorMessage}"
            }, cancellationToken);

            return BadRequest(new ConvertSalesOrderToInvoiceResponseDto
            {
                Success = false,
                SalesOrderId = order.Id,
                SalesOrderNumber = order.OrderNumber,
                ExternalReference = externalRef,
                Message = queueResult.ErrorMessage ?? "Failed to queue invoice for SAP posting"
            });
        }

        // Step 5: Mark the sales order as fulfilled
        try
        {
            await _salesOrderService.MarkAsFulfilledAsync(order.Id, null, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to mark sales order {OrderId} as fulfilled after queuing invoice. " +
                "Invoice is still queued and will be processed.",
                order.Id);
        }

        _logger.LogInformation(
            "Sales order {OrderNumber} converted to queued invoice: ExternalRef={ExternalRef}, " +
            "ReservationId={ReservationId}, QueueId={QueueId}",
            order.OrderNumber, externalRef,
            reservationResult.Reservation.ReservationId, queueResult.QueueId);

        return Accepted(new ConvertSalesOrderToInvoiceResponseDto
        {
            Success = true,
            Message = "Sales order converted to invoice and queued for SAP posting. Poll the status endpoint to check completion.",
            SalesOrderId = order.Id,
            SalesOrderNumber = order.OrderNumber,
            ExternalReference = externalRef,
            ReservationId = reservationResult.Reservation.ReservationId,
            QueueId = queueResult.QueueId,
            Status = "Pending",
            EstimatedProcessingSeconds = 15,
            StatusUrl = Url.Action(nameof(GetQueueStatus), new { externalReference = externalRef })
        });
    }

    #endregion

    #region Direct Stock Transfers

    /// <summary>
    /// Creates an inventory transfer directly in SAP (not queued).
    /// Use this for immediate transfers where queuing is not needed.
    /// For batch processing, prefer the queued endpoint (POST /transfers/queued).
    /// </summary>
    /// <param name="request">The transfer request</param>
    /// <remarks>
    /// This endpoint:
    /// 1. Validates stock availability in the source warehouse
    /// 2. Posts the transfer directly to SAP
    /// 3. Returns the created transfer details
    /// 
    /// If SAP posting fails, no transfer is created.
    /// </remarks>
    [HttpPost("transfers")]
    [ProducesResponseType(typeof(InventoryTransferCreatedResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateTransferDirect(
        [FromBody] CreateDesktopTransferRequest request,
        CancellationToken cancellationToken)
    {
        if (!_sapSettings.Enabled)
            return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });

        if (!ModelState.IsValid)
        {
            return BadRequest(new ErrorResponseDto
            {
                Message = "Validation failed",
                Errors = ModelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => e.ErrorMessage)
                    .ToList()
            });
        }

        _logger.LogInformation("Desktop app creating direct transfer: From={From}, To={To}, Lines={Lines}",
            request.FromWarehouse, request.ToWarehouse, request.Lines.Count);

        // Convert to SAP request
        var sapRequest = new CreateInventoryTransferRequest
        {
            FromWarehouse = request.FromWarehouse,
            ToWarehouse = request.ToWarehouse,
            DocDate = request.DocDate,
            DueDate = request.DueDate,
            Comments = request.Comments,
            Lines = request.Lines.Select(l => new CreateInventoryTransferLineRequest
            {
                ItemCode = l.ItemCode,
                Quantity = l.Quantity,
                FromWarehouseCode = l.FromWarehouseCode ?? request.FromWarehouse,
                ToWarehouseCode = l.WarehouseCode ?? request.ToWarehouse,
                BatchNumbers = l.BatchNumbers,
            }).ToList()
        };

        // Validate stock availability
        var validationResult = await _stockValidation.ValidateInventoryTransferStockAsync(
            sapRequest, cancellationToken);

        if (!validationResult.IsValid)
        {
            return BadRequest(new ErrorResponseDto
            {
                Message = "Stock validation failed",
                Errors = validationResult.Errors.Select(e => e.Message).ToList()
            });
        }

        var transfer = await _sapClient.CreateInventoryTransferAsync(sapRequest, cancellationToken);

        return CreatedAtAction(nameof(GetTransfer), new { docEntry = transfer.DocEntry },
            new InventoryTransferCreatedResponseDto { Transfer = transfer.ToDto() });
    }

    /// <summary>
    /// Validates stock availability for a potential transfer without creating it.
    /// Use this for real-time validation as items are added to a transfer.
    /// </summary>
    /// <param name="request">The transfer lines to validate</param>
    [HttpPost("transfers/validate")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ValidateTransfer(
        [FromBody] CreateDesktopTransferRequest request,
        CancellationToken cancellationToken)
    {
        if (!_sapSettings.Enabled)
            return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });

        if (!ModelState.IsValid)
        {
            return BadRequest(new ErrorResponseDto
            {
                Message = "Validation failed",
                Errors = ModelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => e.ErrorMessage)
                    .ToList()
            });
        }

        var sapRequest = new CreateInventoryTransferRequest
        {
            FromWarehouse = request.FromWarehouse,
            ToWarehouse = request.ToWarehouse,
            Lines = request.Lines.Select(l => new CreateInventoryTransferLineRequest
            {
                ItemCode = l.ItemCode,
                Quantity = l.Quantity,
                FromWarehouseCode = l.FromWarehouseCode ?? request.FromWarehouse,
                ToWarehouseCode = l.WarehouseCode ?? request.ToWarehouse,
            }).ToList()
        };

        var result = await _stockValidation.ValidateInventoryTransferStockAsync(sapRequest, cancellationToken);

        return Ok(new
        {
            isValid = result.IsValid,
            message = result.IsValid ? "Transfer validation successful" : "Transfer validation failed",
            errors = result.Errors.Select(e => new { e.ItemCode, e.WarehouseCode, e.Message }),
            linesValidated = request.Lines.Count
        });
    }

    #endregion

    #region Transfer Retrieval

    /// <summary>
    /// Gets an inventory transfer from SAP by its DocEntry.
    /// </summary>
    /// <param name="docEntry">The SAP DocEntry</param>
    [HttpGet("transfers/{docEntry:int}")]
    [ProducesResponseType(typeof(InventoryTransferDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetTransfer(int docEntry, CancellationToken cancellationToken)
    {
        if (!_sapSettings.Enabled)
            return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });

        var transfer = await _sapClient.GetInventoryTransferByDocEntryAsync(docEntry, cancellationToken);
        if (transfer == null)
            return NotFound(new ErrorResponseDto { Message = $"Transfer with DocEntry {docEntry} not found" });

        return Ok(transfer.ToDto());
    }

    /// <summary>
    /// Gets inventory transfers for a specific warehouse.
    /// </summary>
    /// <param name="warehouseCode">The destination warehouse code</param>
    [HttpGet("transfers/warehouse/{warehouseCode}")]
    [ProducesResponseType(typeof(List<InventoryTransferDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetTransfersByWarehouse(
        string warehouseCode,
        CancellationToken cancellationToken)
    {
        if (!_sapSettings.Enabled)
            return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });

        var transfers = await _sapClient.GetInventoryTransfersToWarehouseAsync(warehouseCode, cancellationToken);
        return Ok(transfers.ToDto());
    }

    /// <summary>
    /// Gets inventory transfers for a warehouse within a date range.
    /// </summary>
    /// <param name="warehouseCode">The destination warehouse code</param>
    /// <param name="fromDate">Start date (yyyy-MM-dd)</param>
    /// <param name="toDate">End date (yyyy-MM-dd)</param>
    [HttpGet("transfers/warehouse/{warehouseCode}/date-range")]
    [ProducesResponseType(typeof(List<InventoryTransferDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetTransfersByDateRange(
        string warehouseCode,
        [FromQuery] DateTime fromDate,
        [FromQuery] DateTime toDate,
        CancellationToken cancellationToken = default)
    {
        if (!_sapSettings.Enabled)
            return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });

        if (fromDate > toDate)
            return BadRequest(new ErrorResponseDto { Message = "fromDate must be before or equal to toDate" });

        var transfers = await _sapClient.GetInventoryTransfersByDateRangeAsync(
            warehouseCode, fromDate, toDate, cancellationToken);
        return Ok(transfers.ToDto());
    }

    /// <summary>
    /// Gets inventory transfers for a warehouse with pagination.
    /// </summary>
    /// <param name="warehouseCode">The destination warehouse code</param>
    /// <param name="page">Page number (1-based, default 1)</param>
    /// <param name="pageSize">Page size (max 100, default 20)</param>
    [HttpGet("transfers/warehouse/{warehouseCode}/paged")]
    [ProducesResponseType(typeof(List<InventoryTransferDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetPagedTransfers(
        string warehouseCode,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (!_sapSettings.Enabled)
            return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var transfers = await _sapClient.GetPagedInventoryTransfersToWarehouseAsync(
            warehouseCode, page, pageSize, cancellationToken);
        return Ok(transfers.ToDto());
    }

    #endregion

    #region Transfer Requests (Approval Workflow)

    /// <summary>
    /// Creates an inventory transfer request that requires approval before execution.
    /// Use this when transfers need managerial approval before stock is moved.
    /// </summary>
    /// <param name="request">The transfer request details</param>
    [HttpPost("transfer-requests")]
    [ProducesResponseType(typeof(InventoryTransferRequestDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateTransferRequest(
        [FromBody] CreateDesktopTransferRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!_sapSettings.Enabled)
            return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });

        if (!ModelState.IsValid)
        {
            return BadRequest(new ErrorResponseDto
            {
                Message = "Validation failed",
                Errors = ModelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => e.ErrorMessage)
                    .ToList()
            });
        }

        var createdBy = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("client_id")?.Value;

        _logger.LogInformation("Desktop app creating transfer request: From={From}, To={To}, CreatedBy={CreatedBy}",
            request.FromWarehouse, request.ToWarehouse, createdBy);

        var sapRequest = new CreateTransferRequestDto
        {
            FromWarehouse = request.FromWarehouse,
            ToWarehouse = request.ToWarehouse,
            DocDate = request.DocDate,
            DueDate = request.DueDate,
            Comments = request.Comments,
            RequesterEmail = request.RequesterEmail,
            RequesterName = request.RequesterName ?? createdBy,
            RequesterBranch = request.RequesterBranch,
            RequesterDepartment = request.RequesterDepartment,
            Lines = request.Lines.Select(l => new CreateTransferRequestLineDto
            {
                ItemCode = l.ItemCode,
                Quantity = l.Quantity,
                FromWarehouseCode = l.FromWarehouseCode ?? request.FromWarehouse,
                ToWarehouseCode = l.ToWarehouseCode ?? request.ToWarehouse
            }).ToList()
        };

        var transferRequest = await _sapClient.CreateInventoryTransferRequestAsync(sapRequest, cancellationToken);

        return CreatedAtAction(nameof(GetTransferRequest), new { docEntry = transferRequest.DocEntry },
            transferRequest.ToDto());
    }

    /// <summary>
    /// Gets an inventory transfer request by its DocEntry.
    /// </summary>
    /// <param name="docEntry">The SAP DocEntry</param>
    [HttpGet("transfer-requests/{docEntry:int}")]
    [ProducesResponseType(typeof(InventoryTransferRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetTransferRequest(int docEntry, CancellationToken cancellationToken)
    {
        if (!_sapSettings.Enabled)
            return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });

        var transferRequest = await _sapClient.GetInventoryTransferRequestByDocEntryAsync(docEntry, cancellationToken);
        if (transferRequest == null)
            return NotFound(new ErrorResponseDto { Message = $"Transfer request with DocEntry {docEntry} not found" });

        return Ok(transferRequest.ToDto());
    }

    /// <summary>
    /// Gets inventory transfer requests for a specific warehouse.
    /// </summary>
    /// <param name="warehouseCode">The warehouse code</param>
    [HttpGet("transfer-requests/warehouse/{warehouseCode}")]
    [ProducesResponseType(typeof(List<InventoryTransferRequestDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetTransferRequestsByWarehouse(
        string warehouseCode,
        CancellationToken cancellationToken)
    {
        if (!_sapSettings.Enabled)
            return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });

        var requests = await _sapClient.GetInventoryTransferRequestsByWarehouseAsync(warehouseCode, cancellationToken);
        return Ok(requests.Select(r => r.ToDto()).ToList());
    }

    /// <summary>
    /// Gets inventory transfer requests with pagination.
    /// </summary>
    /// <param name="page">Page number (1-based, default 1)</param>
    /// <param name="pageSize">Page size (max 100, default 20)</param>
    [HttpGet("transfer-requests/paged")]
    [ProducesResponseType(typeof(List<InventoryTransferRequestDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetPagedTransferRequests(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (!_sapSettings.Enabled)
            return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var requests = await _sapClient.GetPagedInventoryTransferRequestsAsync(page, pageSize, cancellationToken);
        return Ok(requests.Select(r => r.ToDto()).ToList());
    }

    /// <summary>
    /// Converts an approved inventory transfer request into an actual inventory transfer.
    /// This posts the transfer to SAP and moves the stock.
    /// </summary>
    /// <param name="docEntry">The transfer request DocEntry to convert</param>
    [HttpPost("transfer-requests/{docEntry:int}/convert")]
    [ProducesResponseType(typeof(InventoryTransferCreatedResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ConvertTransferRequest(int docEntry, CancellationToken cancellationToken)
    {
        if (!_sapSettings.Enabled)
            return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });

        // Verify the request exists first
        var transferRequest = await _sapClient.GetInventoryTransferRequestByDocEntryAsync(docEntry, cancellationToken);
        if (transferRequest == null)
            return NotFound(new ErrorResponseDto { Message = $"Transfer request with DocEntry {docEntry} not found" });

        _logger.LogInformation("Desktop app converting transfer request {DocEntry} to transfer", docEntry);

        var transfer = await _sapClient.ConvertTransferRequestToTransferAsync(docEntry, cancellationToken);

        return CreatedAtAction(nameof(GetTransfer), new { docEntry = transfer.DocEntry },
            new InventoryTransferCreatedResponseDto
            {
                Message = $"Transfer request {docEntry} converted to transfer successfully",
                Transfer = transfer.ToDto()
            });
    }

    /// <summary>
    /// Closes an inventory transfer request so it can no longer be converted.
    /// Use this to reject or cancel a pending transfer request.
    /// </summary>
    /// <param name="docEntry">The transfer request DocEntry to close</param>
    [HttpPost("transfer-requests/{docEntry:int}/close")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CloseTransferRequest(int docEntry, CancellationToken cancellationToken)
    {
        if (!_sapSettings.Enabled)
            return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });

        var transferRequest = await _sapClient.GetInventoryTransferRequestByDocEntryAsync(docEntry, cancellationToken);
        if (transferRequest == null)
            return NotFound(new ErrorResponseDto { Message = $"Transfer request with DocEntry {docEntry} not found" });

        _logger.LogInformation("Desktop app closing transfer request {DocEntry}", docEntry);

        await _sapClient.CloseInventoryTransferRequestAsync(docEntry, cancellationToken);
        return NoContent();
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

/// <summary>
/// Request to convert a sales order to an invoice with optional modifications.
/// Allows changing quantities, prices, removing lines, or adding new lines.
/// </summary>
public class ConvertSalesOrderToInvoiceRequest
{
    /// <summary>
    /// The local sales order ID to convert
    /// </summary>
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Sales order ID is required")]
    public int SalesOrderId { get; set; }

    /// <summary>
    /// Optional external reference for tracking (auto-generated if not provided)
    /// </summary>
    public string? ExternalReferenceId { get; set; }

    public string? SourceSystem { get; set; }

    /// <summary>
    /// Override document date (defaults to today)
    /// </summary>
    public string? DocDate { get; set; }

    /// <summary>
    /// Override due date (defaults to order delivery date)
    /// </summary>
    public string? DocDueDate { get; set; }

    /// <summary>
    /// Customer reference number (e.g. PO number)
    /// </summary>
    public string? NumAtCard { get; set; }

    /// <summary>
    /// Additional comments for the invoice
    /// </summary>
    public string? Comments { get; set; }

    /// <summary>
    /// Override currency (defaults to order currency)
    /// </summary>
    public string? DocCurrency { get; set; }

    /// <summary>
    /// Override sales person code (defaults to order sales person)
    /// </summary>
    public int? SalesPersonCode { get; set; }

    /// <summary>
    /// Whether to fiscalize the invoice via REVMax
    /// </summary>
    public bool Fiscalize { get; set; } = true;

    /// <summary>
    /// The invoice lines. If null/empty, uses the original sales order lines as-is.
    /// If provided, these lines REPLACE the original order lines entirely — 
    /// allowing quantity changes, price adjustments, line removals, or new line additions.
    /// </summary>
    public List<CreateDesktopInvoiceLineRequest>? Lines { get; set; }
}

/// <summary>
/// Response for sales order to invoice conversion
/// </summary>
public class ConvertSalesOrderToInvoiceResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int SalesOrderId { get; set; }
    public string? SalesOrderNumber { get; set; }
    public string ExternalReference { get; set; } = string.Empty;
    public string? ReservationId { get; set; }
    public int? QueueId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int EstimatedProcessingSeconds { get; set; }
    public string? StatusUrl { get; set; }
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Request to create an inventory transfer request (requires approval) from desktop app
/// </summary>
public class CreateDesktopTransferRequestDto
{
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Source warehouse is required")]
    public string FromWarehouse { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Destination warehouse is required")]
    public string ToWarehouse { get; set; } = string.Empty;

    public string? DocDate { get; set; }
    public string? DueDate { get; set; }
    public string? Comments { get; set; }
    public string? RequesterEmail { get; set; }
    public string? RequesterName { get; set; }
    public int? RequesterBranch { get; set; }
    public int? RequesterDepartment { get; set; }

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "At least one line item is required")]
    [System.ComponentModel.DataAnnotations.MinLength(1)]
    public List<CreateDesktopTransferRequestLineDto> Lines { get; set; } = new();
}

/// <summary>
/// Line item for desktop transfer request
/// </summary>
public class CreateDesktopTransferRequestLineDto
{
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Item code is required")]
    public string ItemCode { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Range(0.000001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero")]
    public decimal Quantity { get; set; }

    public string? FromWarehouseCode { get; set; }
    public string? ToWarehouseCode { get; set; }
}

#endregion
