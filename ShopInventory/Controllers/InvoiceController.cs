using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class InvoiceController : ControllerBase
{
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly IStockValidationService _stockValidation;
    private readonly IBatchInventoryValidationService _batchValidation;
    private readonly IInventoryLockService _lockService;
    private readonly IFiscalizationService _fiscalizationService;
    private readonly IDocumentService _documentService;
    private readonly SAPSettings _settings;
    private readonly ILogger<InvoiceController> _logger;

    public InvoiceController(
        ISAPServiceLayerClient sapClient,
        IStockValidationService stockValidation,
        IBatchInventoryValidationService batchValidation,
        IInventoryLockService lockService,
        IFiscalizationService fiscalizationService,
        IDocumentService documentService,
        IOptions<SAPSettings> settings,
        ILogger<InvoiceController> logger)
    {
        _sapClient = sapClient;
        _stockValidation = stockValidation;
        _batchValidation = batchValidation;
        _lockService = lockService;
        _fiscalizationService = fiscalizationService;
        _documentService = documentService;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new invoice in SAP Business One with comprehensive batch validation.
    /// For batch-managed items:
    /// - Validates batch allocations at the batch level (not just warehouse)
    /// - Auto-allocates batches using FIFO/FEFO if not explicitly specified
    /// - Prevents negative quantities at both batch and warehouse level
    /// - Uses short-lived locks to prevent race conditions
    /// </summary>
    /// <param name="request">The invoice creation request</param>
    /// <param name="autoAllocateBatches">Whether to auto-allocate batches using FIFO/FEFO (default: true)</param>
    /// <param name="allocationStrategy">Batch allocation strategy: FEFO (default) or FIFO</param>
    /// <returns>The created invoice with batch allocation details</returns>
    [HttpPost]
    [ProducesResponseType(typeof(InvoiceCreatedResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(BatchStockValidationResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CreateInvoice(
        [FromBody] CreateInvoiceRequest request,
        [FromQuery] bool autoAllocateBatches = true,
        [FromQuery] BatchAllocationStrategy allocationStrategy = BatchAllocationStrategy.FEFO,
        CancellationToken cancellationToken = default)
    {
        List<string>? acquiredLockTokens = null;

        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            // Step 1: Validate the model
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

            // Step 2: Validate basic quantities (positive, non-zero)
            var quantityErrors = ValidateQuantities(request);
            if (quantityErrors.Count > 0)
            {
                return BadRequest(new ErrorResponseDto
                {
                    Message = "Quantity validation failed",
                    Errors = quantityErrors
                });
            }

            // Step 3: Validate warehouse codes are present (REQUIRED for batch validation)
            var warehouseErrors = ValidateWarehouseCodes(request);
            if (warehouseErrors.Count > 0)
            {
                return BadRequest(new BatchStockValidationResponseDto
                {
                    Message = "Warehouse validation failed - warehouse code is required for each line",
                    IsValid = false,
                    Errors = warehouseErrors.Select(e => new BatchValidationErrorDto
                    {
                        ErrorCode = BatchValidationErrorCode.WarehouseRequired,
                        Message = e,
                        SuggestedAction = "Specify a warehouse code for each invoice line"
                    }).ToList(),
                    Suggestions = new List<string>
                    {
                        "Each invoice line must have an explicit warehouse code",
                        "Use GET /api/Warehouses to see available warehouses"
                    }
                });
            }

            _logger.LogInformation(
                "Validating batch stock for invoice with {LineCount} lines. AutoAllocate: {AutoAllocate}, Strategy: {Strategy}",
                request.Lines?.Count ?? 0, autoAllocateBatches, allocationStrategy);

            // Step 4: CRITICAL - Batch-level validation with FIFO/FEFO auto-allocation
            var batchValidationResult = await _batchValidation.ValidateAndAllocateBatchesAsync(
                request,
                autoAllocateBatches,
                allocationStrategy,
                cancellationToken);

            if (!batchValidationResult.IsValid)
            {
                _logger.LogWarning(
                    "Batch validation failed for invoice creation. {ErrorCount} errors. Strategy: {Strategy}",
                    batchValidationResult.ValidationErrors.Count, allocationStrategy);

                return BadRequest(new BatchStockValidationResponseDto
                {
                    Message = "Batch validation failed - would cause negative quantities",
                    IsValid = false,
                    Errors = batchValidationResult.ValidationErrors,
                    Warnings = batchValidationResult.Warnings,
                    Suggestions = batchValidationResult.Suggestions.Count > 0
                        ? batchValidationResult.Suggestions
                        : new List<string>
                        {
                            "Check stock levels using GET /api/Stock/{warehouseCode}",
                            "For batch-managed items, use GET /api/Product/{itemCode}/batches/{warehouseCode} to see available batches",
                            "Consider enabling auto-allocation with FIFO/FEFO strategy",
                            "Reduce quantities or request an inventory transfer"
                        }
                });
            }

            // Step 5: Apply auto-allocated batches to the request
            if (batchValidationResult.BatchesAutoAllocated && batchValidationResult.AllocatedLines.Count > 0)
            {
                ApplyAllocatedBatchesToRequest(request, batchValidationResult.AllocatedLines);
                _logger.LogInformation(
                    "Applied auto-allocated batches to {LineCount} lines using {Strategy} strategy",
                    batchValidationResult.AllocatedLines.Count, allocationStrategy);
            }

            // Step 6: CRITICAL - Pre-post validation with locks (Rule 4: Concurrency mitigation)
            var prePostResult = await _batchValidation.PrePostValidationAsync(
                request,
                batchValidationResult.AllocatedLines,
                cancellationToken);

            if (!prePostResult.IsValid)
            {
                // Check if it's a lock conflict
                var lockErrors = prePostResult.Errors
                    .Where(e => e.ErrorCode == BatchValidationErrorCode.LockAcquisitionFailed)
                    .ToList();

                if (lockErrors.Count > 0)
                {
                    _logger.LogWarning("Lock acquisition failed for invoice creation - concurrent access detected");

                    return Conflict(new BatchStockValidationResponseDto
                    {
                        Message = "Concurrent access detected - another operation is in progress for these items",
                        IsValid = false,
                        Errors = prePostResult.Errors,
                        Suggestions = new List<string>
                        {
                            "Another user may be processing an invoice for the same items",
                            "Please wait a few seconds and try again",
                            "If the problem persists, check for stuck transactions"
                        }
                    });
                }

                _logger.LogWarning(
                    "Pre-post validation failed - stock may have changed since initial validation. {ErrorCount} errors",
                    prePostResult.Errors.Count);

                return BadRequest(new BatchStockValidationResponseDto
                {
                    Message = "Pre-post validation failed - stock levels changed during processing",
                    IsValid = false,
                    Errors = prePostResult.Errors,
                    Warnings = prePostResult.Warnings,
                    Suggestions = prePostResult.Suggestions
                });
            }

            // Store lock tokens for cleanup
            if (!string.IsNullOrEmpty(prePostResult.LockToken))
            {
                acquiredLockTokens = new List<string> { prePostResult.LockToken };
            }

            // Step 7: Also validate with SAP for additional safety (belt and suspenders)
            var stockValidationErrors = await _sapClient.ValidateStockAvailabilityAsync(request, cancellationToken);
            if (stockValidationErrors.Count > 0)
            {
                _logger.LogWarning(
                    "SAP stock validation failed after batch validation. {ErrorCount} items have insufficient stock",
                    stockValidationErrors.Count);

                return BadRequest(new StockValidationResponseDto
                {
                    Message = "SAP stock validation failed - insufficient stock",
                    IsValid = false,
                    Errors = stockValidationErrors,
                    Suggestions = new List<string>
                    {
                        "Stock levels may have been updated in SAP",
                        "Please refresh and try again"
                    }
                });
            }

            // Step 8: POST to SAP (with locks held)
            var invoice = await _sapClient.CreateInvoiceAsync(request, cancellationToken);

            _logger.LogInformation(
                "Invoice created successfully in SAP. DocEntry: {DocEntry}, DocNum: {DocNum}, Customer: {CardCode}, " +
                "BatchesAllocated: {BatchCount}, Strategy: {Strategy}",
                invoice.DocEntry, invoice.DocNum, invoice.CardCode,
                batchValidationResult.AllocatedLines.Sum(l => l.Batches.Count),
                allocationStrategy);

            // Step 9: FISCALIZE with REVMax after successful SAP posting
            FiscalizationResult? fiscalizationResult = null;
            try
            {
                fiscalizationResult = await _fiscalizationService.FiscalizeInvoiceAsync(
                    invoice.ToDto(),
                    new CustomerFiscalDetails
                    {
                        CustomerName = invoice.CardName
                    },
                    cancellationToken);

                if (fiscalizationResult.Success)
                {
                    _logger.LogInformation(
                        "Invoice {DocNum} fiscalized successfully. QRCode: {HasQR}, ReceiptGlobalNo: {ReceiptNo}",
                        invoice.DocNum,
                        !string.IsNullOrEmpty(fiscalizationResult.QRCode),
                        fiscalizationResult.ReceiptGlobalNo);
                }
                else
                {
                    _logger.LogWarning(
                        "Invoice {DocNum} fiscalization failed: {Message}. Invoice was created in SAP but not fiscalized.",
                        invoice.DocNum, fiscalizationResult.Message);
                }
            }
            catch (Exception fiscalEx)
            {
                // Log but don't fail the request - SAP invoice was created successfully
                _logger.LogError(fiscalEx,
                    "Error during fiscalization of invoice {DocNum}. Invoice was created in SAP but fiscalization failed.",
                    invoice.DocNum);

                fiscalizationResult = new FiscalizationResult
                {
                    Success = false,
                    Message = "Fiscalization error - invoice created in SAP",
                    ErrorDetails = fiscalEx.Message
                };
            }

            return CreatedAtAction(
                nameof(GetInvoiceByDocEntry),
                new { docEntry = invoice.DocEntry },
                new InvoiceCreatedResponseDto
                {
                    Message = fiscalizationResult?.Success == true
                        ? "Invoice created and fiscalized successfully"
                        : "Invoice created successfully (fiscalization pending)",
                    Invoice = invoice.ToDto(),
                    Fiscalization = fiscalizationResult
                });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Validation error creating invoice");
            return BadRequest(new ErrorResponseDto { Message = "Validation error", Errors = ex.Message.Split("; ").ToList() });
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return StatusCode(504, new ErrorResponseDto { Message = "Connection to SAP Service Layer timed out. Please check network connectivity to the SAP server." });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return StatusCode(502, new ErrorResponseDto { Message = "Unable to connect to SAP Service Layer. Please check network connectivity.", Errors = new List<string> { ex.Message } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating invoice");
            return StatusCode(500, new ErrorResponseDto { Message = "Error creating invoice", Errors = new List<string> { ex.Message } });
        }
        finally
        {
            // CRITICAL: Always release locks after processing
            if (acquiredLockTokens != null && acquiredLockTokens.Count > 0)
            {
                try
                {
                    await _lockService.ReleaseMultipleLocksAsync(acquiredLockTokens);
                    _logger.LogDebug("Released {LockCount} inventory locks after invoice processing", acquiredLockTokens.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to release inventory locks - they will expire automatically");
                }
            }
        }
    }

    /// <summary>
    /// Gets available batches for an item in a warehouse, sorted by FIFO/FEFO strategy.
    /// Use this endpoint to see which batches are available before creating an invoice.
    /// </summary>
    /// <param name="itemCode">The item code</param>
    /// <param name="warehouseCode">The warehouse code</param>
    /// <param name="strategy">Sorting strategy: FEFO (default) or FIFO</param>
    /// <returns>List of available batches sorted by the specified strategy</returns>
    [HttpGet("{itemCode}/batches/{warehouseCode}")]
    [ProducesResponseType(typeof(List<AvailableBatchDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAvailableBatches(
        string itemCode,
        string warehouseCode,
        [FromQuery] BatchAllocationStrategy strategy = BatchAllocationStrategy.FEFO,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            var batches = await _batchValidation.GetAvailableBatchesAsync(
                itemCode, warehouseCode, strategy, cancellationToken);

            if (batches.Count == 0)
            {
                return NotFound(new ErrorResponseDto
                {
                    Message = $"No batches found for item '{itemCode}' in warehouse '{warehouseCode}'"
                });
            }

            return Ok(new
            {
                itemCode,
                warehouseCode,
                strategy = strategy.ToString(),
                batchCount = batches.Count,
                totalAvailable = batches.Sum(b => b.AvailableQuantity),
                batches
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting available batches for {ItemCode} in {Warehouse}",
                itemCode, warehouseCode);
            return StatusCode(500, new ErrorResponseDto
            {
                Message = "Error retrieving batch information",
                Errors = new List<string> { ex.Message }
            });
        }
    }

    /// <summary>
    /// Validates an invoice request without creating it.
    /// Use this endpoint to check if an invoice would be valid before submitting.
    /// </summary>
    /// <param name="request">The invoice request to validate</param>
    /// <param name="autoAllocateBatches">Whether to simulate auto-allocation</param>
    /// <param name="allocationStrategy">Batch allocation strategy to simulate</param>
    /// <returns>Validation result with allocated batches (if successful)</returns>
    [HttpPost("validate")]
    [ProducesResponseType(typeof(BatchAllocationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BatchStockValidationResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ValidateInvoice(
        [FromBody] CreateInvoiceRequest request,
        [FromQuery] bool autoAllocateBatches = true,
        [FromQuery] BatchAllocationStrategy allocationStrategy = BatchAllocationStrategy.FEFO,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            // Basic validation
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

            var warehouseErrors = ValidateWarehouseCodes(request);
            if (warehouseErrors.Count > 0)
            {
                return BadRequest(new BatchStockValidationResponseDto
                {
                    Message = "Warehouse validation failed",
                    IsValid = false,
                    Errors = warehouseErrors.Select(e => new BatchValidationErrorDto
                    {
                        ErrorCode = BatchValidationErrorCode.WarehouseRequired,
                        Message = e,
                        SuggestedAction = "Specify a warehouse code for each invoice line"
                    }).ToList()
                });
            }

            var result = await _batchValidation.ValidateAndAllocateBatchesAsync(
                request,
                autoAllocateBatches,
                allocationStrategy,
                cancellationToken);

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating invoice");
            return StatusCode(500, new ErrorResponseDto
            {
                Message = "Error validating invoice",
                Errors = new List<string> { ex.Message }
            });
        }
    }

    /// <summary>
    /// Gets an invoice by its DocEntry
    /// </summary>
    /// <param name="docEntry">The document entry number</param>
    /// <returns>The invoice details</returns>
    [HttpGet("{docEntry:int}")]
    [ProducesResponseType(typeof(InvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetInvoiceByDocEntry(
        int docEntry,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            var invoice = await _sapClient.GetInvoiceByDocEntryAsync(docEntry, cancellationToken);

            if (invoice == null)
            {
                return NotFound(new ErrorResponseDto { Message = $"Invoice with DocEntry {docEntry} not found" });
            }

            return Ok(invoice.ToDto());
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return StatusCode(504, new ErrorResponseDto { Message = "Connection to SAP Service Layer timed out." });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return StatusCode(502, new ErrorResponseDto { Message = "Unable to connect to SAP Service Layer.", Errors = new List<string> { ex.Message } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving invoice {DocEntry}", docEntry);
            return StatusCode(500, new ErrorResponseDto { Message = "Error retrieving invoice", Errors = new List<string> { ex.Message } });
        }
    }

    /// <summary>
    /// Gets all invoices for a specific customer
    /// </summary>
    /// <param name="cardCode">The customer code</param>
    /// <returns>List of invoices</returns>
    [HttpGet("customer/{cardCode}")]
    [ProducesResponseType(typeof(InvoiceDateResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetInvoicesByCustomer(
        string cardCode,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            if (string.IsNullOrWhiteSpace(cardCode))
            {
                return BadRequest(new ErrorResponseDto { Message = "Customer code is required" });
            }

            var invoices = await _sapClient.GetInvoicesByCustomerAsync(cardCode, cancellationToken);

            _logger.LogInformation("Retrieved {Count} invoices for customer {CardCode}",
                invoices.Count, cardCode);

            return Ok(new
            {
                customer = cardCode,
                count = invoices.Count,
                invoices = invoices.ToDto()
            });
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return StatusCode(504, new ErrorResponseDto { Message = "Connection to SAP Service Layer timed out." });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return StatusCode(502, new ErrorResponseDto { Message = "Unable to connect to SAP Service Layer.", Errors = new List<string> { ex.Message } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving invoices for customer {CardCode}", cardCode);
            return StatusCode(500, new ErrorResponseDto { Message = "Error retrieving invoices", Errors = new List<string> { ex.Message } });
        }
    }

    /// <summary>
    /// Gets attachments for a specific invoice
    /// </summary>
    /// <param name="docEntry">The invoice document entry</param>
    /// <returns>List of attachments</returns>
    [HttpGet("{docEntry:int}/attachments")]
    [ProducesResponseType(typeof(DocumentAttachmentListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetInvoiceAttachments(
        int docEntry,
        CancellationToken cancellationToken)
    {
        var result = await _documentService.GetAttachmentsAsync("Invoice", docEntry, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Download a specific attachment for an invoice
    /// </summary>
    /// <param name="docEntry">The invoice document entry</param>
    /// <param name="attachmentId">The attachment ID</param>
    [HttpGet("{docEntry:int}/attachments/{attachmentId:int}/download")]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadInvoiceAttachment(
        int docEntry,
        int attachmentId,
        CancellationToken cancellationToken)
    {
        var attachments = await _documentService.GetAttachmentsAsync("Invoice", docEntry, cancellationToken);
        if (!attachments.Attachments.Any(a => a.Id == attachmentId))
        {
            return NotFound(new ErrorResponseDto { Message = "Attachment not found" });
        }

        var (stream, fileName, mimeType) = await _documentService.DownloadAttachmentAsync(attachmentId, cancellationToken);
        if (stream == null)
        {
            return NotFound(new ErrorResponseDto { Message = "Attachment not found" });
        }

        return File(stream, mimeType ?? "application/octet-stream", fileName);
    }

    /// <summary>
    /// Gets invoices within a date range
    /// </summary>
    /// <param name="fromDate">Start date (yyyy-MM-dd)</param>
    /// <param name="toDate">End date (yyyy-MM-dd)</param>
    /// <returns>List of invoices</returns>
    [HttpGet("date-range")]
    [ProducesResponseType(typeof(InvoiceDateResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetInvoicesByDateRange(
        [FromQuery] DateTime fromDate,
        [FromQuery] DateTime toDate,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            if (fromDate > toDate)
            {
                return BadRequest(new ErrorResponseDto { Message = "From date cannot be later than to date" });
            }

            var invoices = await _sapClient.GetInvoicesByDateRangeAsync(fromDate, toDate, cancellationToken);

            _logger.LogInformation("Retrieved {Count} invoices between {FromDate} and {ToDate}",
                invoices.Count, fromDate.ToString("yyyy-MM-dd"), toDate.ToString("yyyy-MM-dd"));

            return Ok(new InvoiceDateResponseDto
            {
                FromDate = fromDate.ToString("yyyy-MM-dd"),
                ToDate = toDate.ToString("yyyy-MM-dd"),
                Count = invoices.Count,
                Invoices = invoices.ToDto()
            });
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return StatusCode(504, new ErrorResponseDto { Message = "Connection to SAP Service Layer timed out." });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return StatusCode(502, new ErrorResponseDto { Message = "Unable to connect to SAP Service Layer.", Errors = new List<string> { ex.Message } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving invoices by date range");
            return StatusCode(500, new ErrorResponseDto { Message = "Error retrieving invoices", Errors = new List<string> { ex.Message } });
        }
    }

    /// <summary>
    /// Gets invoices with pagination
    /// </summary>
    /// <param name="page">Page number (default: 1)</param>
    /// <param name="pageSize">Number of records per page (default: 20, max: 100)</param>
    /// <returns>List of invoices with pagination info</returns>
    [HttpGet("paged")]
    [ProducesResponseType(typeof(InvoiceListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetPagedInvoices(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            if (page < 1)
            {
                return BadRequest(new ErrorResponseDto { Message = "Page number must be at least 1" });
            }

            if (pageSize < 1 || pageSize > 100)
            {
                return BadRequest(new ErrorResponseDto { Message = "Page size must be between 1 and 100" });
            }

            var invoices = await _sapClient.GetPagedInvoicesAsync(page, pageSize, cancellationToken);

            _logger.LogInformation("Retrieved page {Page} of invoices ({Count} records, page size: {PageSize})",
                page, invoices.Count, pageSize);

            return Ok(new InvoiceListResponseDto
            {
                Page = page,
                PageSize = pageSize,
                Count = invoices.Count,
                HasMore = invoices.Count == pageSize,
                Invoices = invoices.ToDto()
            });
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return StatusCode(504, new ErrorResponseDto { Message = "Connection to SAP Service Layer timed out." });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return StatusCode(502, new ErrorResponseDto { Message = "Unable to connect to SAP Service Layer.", Errors = new List<string> { ex.Message } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving paged invoices");
            return StatusCode(500, new ErrorResponseDto { Message = "Error retrieving invoices", Errors = new List<string> { ex.Message } });
        }
    }

    #region Private Helper Methods

    /// <summary>
    /// Validates quantities to ensure none are negative or zero
    /// </summary>
    private List<string> ValidateQuantities(CreateInvoiceRequest request)
    {
        var errors = new List<string>();

        if (request.Lines == null || request.Lines.Count == 0)
        {
            errors.Add("At least one line item is required");
            return errors;
        }

        for (int i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];

            if (line.Quantity <= 0)
            {
                errors.Add($"Line {i + 1} (Item: {line.ItemCode ?? "unknown"}): Quantity must be greater than zero. Current value: {line.Quantity}");
            }

            if (line.UnitPrice.HasValue && line.UnitPrice.Value < 0)
            {
                errors.Add($"Line {i + 1} (Item: {line.ItemCode ?? "unknown"}): Unit price cannot be negative. Current value: {line.UnitPrice.Value}");
            }

            // Validate batch quantities if specified
            if (line.BatchNumbers != null)
            {
                for (int j = 0; j < line.BatchNumbers.Count; j++)
                {
                    var batch = line.BatchNumbers[j];
                    if (batch.Quantity <= 0)
                    {
                        errors.Add($"Line {i + 1}, Batch {j + 1} (Batch: {batch.BatchNumber ?? "unknown"}): Quantity must be greater than zero. Current value: {batch.Quantity}");
                    }
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// Validates that all lines have warehouse codes (REQUIRED for batch validation)
    /// </summary>
    private List<string> ValidateWarehouseCodes(CreateInvoiceRequest request)
    {
        var errors = new List<string>();

        if (request.Lines == null)
            return errors;

        for (int i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            if (string.IsNullOrWhiteSpace(line.WarehouseCode))
            {
                errors.Add($"Line {i + 1} (Item: {line.ItemCode ?? "unknown"}): Warehouse code is required for each invoice line.");
            }
        }

        return errors;
    }

    /// <summary>
    /// Applies auto-allocated batches to the invoice request.
    /// This modifies the request to include the batch allocations determined by FIFO/FEFO.
    /// </summary>
    private void ApplyAllocatedBatchesToRequest(
        CreateInvoiceRequest request,
        List<AllocatedBatchLine> allocatedLines)
    {
        if (request.Lines == null)
            return;

        foreach (var allocatedLine in allocatedLines)
        {
            // Find the corresponding line in the request (1-based to 0-based index)
            var lineIndex = allocatedLine.LineNumber - 1;
            if (lineIndex < 0 || lineIndex >= request.Lines.Count)
                continue;

            var requestLine = request.Lines[lineIndex];

            // Only apply if line doesn't already have batch allocations
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
                        })
                        .ToList();

                    _logger.LogDebug(
                        "Applied {BatchCount} batches to line {LineNumber} for item {ItemCode}: {Batches}",
                        allocatedLine.Batches.Count,
                        allocatedLine.LineNumber,
                        allocatedLine.ItemCode,
                        string.Join(", ", allocatedLine.Batches.Select(b => $"{b.BatchNumber}:{b.QuantityAllocated:N2}")));
                }
            }

            // Apply UoM conversion if needed (update quantity to inventory UoM)
            if (allocatedLine.UoMConversionFactor != 1.0m)
            {
                _logger.LogDebug(
                    "Line {LineNumber}: Applied UoM conversion factor {Factor} (Original: {Original}, Converted: {Converted})",
                    allocatedLine.LineNumber,
                    allocatedLine.UoMConversionFactor,
                    allocatedLine.OriginalRequestedQuantity,
                    allocatedLine.TotalQuantityAllocated);
            }
        }
    }

    #endregion
}
