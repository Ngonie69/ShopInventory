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
    private readonly IInvoicePdfService _invoicePdfService;
    private readonly IAuthService _authService;
    private readonly SAPSettings _settings;
    private readonly ILogger<InvoiceController> _logger;

    /// <summary>
    /// Business partners excluded from POD uploads (internal/intercompany accounts).
    /// </summary>
    private static readonly HashSet<string> ExcludedPodCardCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "CIS006", "MAC009", "MAC006", "COR007", "COR006", "COR008",
        "VAN008", "VAN009", "VAN010", "VAN011", "VAN012", "VAN013",
        "VAN014", "VAN015", "VAN016", "VAN017", "VAN018", "VAN019", "VAN020",
        "STA040", "STA041", "STA042", "STA043", "STA044", "STA045", "STA046", "STA047", "STA048",
        "PRO030", "PRO031", "PRO032", "PRO033", "PRO034", "PRO035", "PRO036",
        "CAS004(FCA)", "DON004", "TEA006", "TEA007"
    };

    public InvoiceController(
        ISAPServiceLayerClient sapClient,
        IStockValidationService stockValidation,
        IBatchInventoryValidationService batchValidation,
        IInventoryLockService lockService,
        IFiscalizationService fiscalizationService,
        IDocumentService documentService,
        IInvoicePdfService invoicePdfService,
        IAuthService authService,
        IOptions<SAPSettings> settings,
        ILogger<InvoiceController> logger)
    {
        _sapClient = sapClient;
        _stockValidation = stockValidation;
        _batchValidation = batchValidation;
        _lockService = lockService;
        _fiscalizationService = fiscalizationService;
        _documentService = documentService;
        _invoicePdfService = invoicePdfService;
        _authService = authService;
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
    [Authorize(Roles = "Admin,Cashier")]
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

            // Step 1b: Check for duplicate invoice by U_Van_saleorder
            if (!string.IsNullOrWhiteSpace(request.U_Van_saleorder))
            {
                var existingInvoice = await _sapClient.GetInvoiceByVanSaleOrderAsync(request.U_Van_saleorder, cancellationToken);
                if (existingInvoice != null)
                {
                    _logger.LogWarning(
                        "Duplicate invoice detected. U_Van_saleorder '{VanSaleOrder}' already exists as DocEntry {DocEntry}, DocNum {DocNum}",
                        request.U_Van_saleorder, existingInvoice.DocEntry, existingInvoice.DocNum);

                    return Conflict(new ErrorResponseDto
                    {
                        Message = $"Invoice with U_Van_saleorder '{request.U_Van_saleorder}' already exists in SAP (DocNum: {existingInvoice.DocNum})"
                    });
                }
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
    [Authorize(Roles = "Admin,Cashier,StockController,DepotController,Manager")]
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
    [Authorize(Roles = "Admin,Cashier")]
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
    [Authorize(Roles = "Admin,Cashier,StockController,DepotController,Manager")]
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
    /// Gets an invoice by its DocNum
    /// </summary>
    /// <param name="docNum">The document number</param>
    /// <returns>The invoice details</returns>
    [HttpGet("by-docnum/{docNum:int}")]
    [Authorize(Roles = "Admin,Cashier,PodOperator,Driver,SalesRep")]
    [ProducesResponseType(typeof(InvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetInvoiceByDocNum(
        int docNum,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            var invoice = await _sapClient.GetInvoiceByDocNumAsync(docNum, cancellationToken);

            if (invoice == null)
            {
                return NotFound(new ErrorResponseDto { Message = $"Invoice with DocNum {docNum} not found" });
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
            _logger.LogError(ex, "Error retrieving invoice by DocNum {DocNum}", docNum);
            return StatusCode(500, new ErrorResponseDto { Message = "Error retrieving invoice", Errors = new List<string> { ex.Message } });
        }
    }

    /// <summary>
    /// Downloads the invoice as a formatted A4 PDF (Fiscal Tax Invoice)
    /// </summary>
    /// <param name="docEntry">The document entry number</param>
    /// <returns>PDF file</returns>
    [HttpGet("{docEntry:int}/pdf")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> DownloadInvoicePdf(
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

            var invoiceDto = invoice.ToDto();

            // Enrich with business partner details (VAT, TIN, phone, email)
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
            _logger.LogError(ex, "Error generating PDF for invoice {DocEntry}", docEntry);
            return StatusCode(500, new ErrorResponseDto { Message = "Error generating invoice PDF", Errors = new List<string> { ex.Message } });
        }
    }

    /// <summary>
    /// Gets all invoices for a specific customer
    /// </summary>
    /// <param name="cardCode">The customer code</param>
    /// <param name="fromDate">Optional start date filter</param>
    /// <param name="toDate">Optional end date filter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="page">Optional page number for paged results</param>
    /// <param name="pageSize">Optional page size for paged results</param>
    /// <returns>List of invoices</returns>
    [HttpGet("customer/{cardCode}")]
    [Authorize(Roles = "Admin,Cashier,StockController,DepotController,Manager")]
    [ProducesResponseType(typeof(InvoiceDateResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetInvoicesByCustomer(
        string cardCode,
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        CancellationToken cancellationToken = default,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null)
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

            var filterFromDate = fromDate.HasValue && toDate.HasValue ? fromDate : null;
            var filterToDate = fromDate.HasValue && toDate.HasValue ? toDate : null;
            var usePagination = page.HasValue || pageSize.HasValue;

            List<Invoice> invoices;
            int currentPage;
            int currentPageSize;
            int totalCount;
            int totalPages;
            bool hasMore;

            if (usePagination)
            {
                currentPage = Math.Max(page ?? 1, 1);
                currentPageSize = Math.Clamp(pageSize ?? 20, 1, 100);
                var skip = (currentPage - 1) * currentPageSize;

                invoices = await _sapClient.GetPagedInvoicesByOffsetAsync(
                    skip,
                    currentPageSize,
                    null,
                    cardCode,
                    filterFromDate,
                    filterToDate,
                    cancellationToken);

                totalCount = await _sapClient.GetInvoicesCountAsync(
                    null,
                    cardCode,
                    filterFromDate,
                    filterToDate,
                    cancellationToken);
                totalPages = currentPageSize > 0 ? (int)Math.Ceiling(totalCount / (double)currentPageSize) : 1;
                hasMore = (currentPage * currentPageSize) < totalCount;
            }
            else if (filterFromDate.HasValue && filterToDate.HasValue)
            {
                invoices = await _sapClient.GetInvoicesByCustomerAsync(cardCode, filterFromDate.Value, filterToDate.Value, cancellationToken);
                currentPage = 1;
                currentPageSize = invoices.Count;
                totalCount = invoices.Count;
                totalPages = invoices.Count > 0 ? 1 : 0;
                hasMore = false;
            }
            else
            {
                invoices = await _sapClient.GetInvoicesByCustomerAsync(cardCode, cancellationToken);
                currentPage = 1;
                currentPageSize = invoices.Count;
                totalCount = invoices.Count;
                totalPages = invoices.Count > 0 ? 1 : 0;
                hasMore = false;
            }

            _logger.LogInformation("Retrieved {Count} invoices for customer {CardCode}",
                invoices.Count, cardCode);

            return Ok(new InvoiceDateResponseDto
            {
                Customer = cardCode,
                FromDate = filterFromDate?.ToString("yyyy-MM-dd"),
                ToDate = filterToDate?.ToString("yyyy-MM-dd"),
                Page = currentPage,
                PageSize = currentPageSize,
                Count = invoices.Count,
                TotalCount = totalCount,
                TotalPages = totalPages,
                HasMore = hasMore,
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
    [Authorize(Roles = "Admin,Cashier,PodOperator,Driver,SalesRep")]
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
    [Authorize(Roles = "Admin,Cashier,PodOperator,Driver,SalesRep")]
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
    /// Upload a Proof of Delivery (POD) attachment for an invoice.
    /// Designed for mobile app consumption with multipart/form-data.
    /// </summary>
    /// <param name="docEntry">The invoice document entry</param>
    /// <param name="file">The POD file (image or PDF)</param>
    /// <param name="description">Optional description</param>
    [HttpPost("{docEntry:int}/pod")]
    [Authorize(Roles = "Admin,Cashier,PodOperator,Driver,SalesRep")]
    [ProducesResponseType(typeof(DocumentAttachmentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [RequestSizeLimit(20 * 1024 * 1024)] // 20MB max
    public async Task<IActionResult> UploadPod(
        int docEntry,
        IFormFile file,
        [FromForm] string? description = null,
        [FromForm] string? uploadedByUsername = null,
        CancellationToken cancellationToken = default)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new ErrorResponseDto { Message = "No file uploaded" });
        }

        // Validate file type - only images and PDFs allowed
        var allowedTypes = new[] { "image/jpeg", "image/png", "image/webp", "application/pdf" };
        if (!allowedTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest(new ErrorResponseDto
            {
                Message = "Invalid file type. Only JPEG, PNG, WebP images and PDF files are allowed."
            });
        }

        // Validate that this invoice's BP is not excluded from PODs
        Invoice? invoiceInfo = null;
        try
        {
            invoiceInfo = await _sapClient.GetInvoiceByDocEntryAsync(docEntry, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch invoice info for DocEntry {DocEntry} during POD upload", docEntry);
        }

        if (invoiceInfo != null && ExcludedPodCardCodes.Contains(invoiceInfo.CardCode ?? ""))
        {
            return BadRequest(new ErrorResponseDto
            {
                Message = $"POD uploads are not required for {invoiceInfo.CardName} ({invoiceInfo.CardCode})"
            });
        }

        var request = new UploadAttachmentRequest
        {
            EntityType = "Invoice",
            EntityId = docEntry,
            Description = string.IsNullOrWhiteSpace(description)
                ? "POD - Proof of Delivery"
                : $"POD - {description}",
            IncludeInEmail = false
        };

        // Cache invoice info from SAP so POD listings can display DocNum and Customer
        if (invoiceInfo != null)
        {
            try
            {
                await _documentService.EnsureInvoiceCachedAsync(
                    docEntry, invoiceInfo.DocNum, invoiceInfo.CardCode, invoiceInfo.CardName, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not cache invoice info for DocEntry {DocEntry} during POD upload", docEntry);
            }
        }

        // Resolve user ID: prefer JWT claim, fall back to username lookup (API key auth)
        var userId = GetUserId();
        if (userId == null && !string.IsNullOrWhiteSpace(uploadedByUsername))
        {
            var user = await _authService.GetUserByUsernameAsync(uploadedByUsername);
            userId = user?.Id;
        }

        using var stream = file.OpenReadStream();

        // Prefix filename with POD_ to make it filterable
        var fileName = file.FileName.StartsWith("POD", StringComparison.OrdinalIgnoreCase)
            ? file.FileName
            : $"POD_{file.FileName}";

        var attachment = await _documentService.UploadAttachmentAsync(
            request, stream, fileName, file.ContentType, userId, cancellationToken);

        _logger.LogInformation("POD uploaded for invoice {DocEntry} by user {UserId}", docEntry, userId);

        // Best-effort: also push the POD to SAP as an attachment on the invoice
        try
        {
            var (fileStream, _, _) = await _documentService.DownloadAttachmentAsync(attachment.Id, cancellationToken);
            if (fileStream != null)
            {
                using (fileStream)
                {
                    if (invoiceInfo?.AttachmentEntry is int existingAttachmentEntry && existingAttachmentEntry > 0)
                    {
                        _logger.LogInformation(
                            "Appending POD to existing SAP attachment {AttachmentEntry} for invoice {DocEntry}...",
                            existingAttachmentEntry,
                            docEntry);
                        await _sapClient.AppendAttachmentToSAPAsync(existingAttachmentEntry, fileStream, fileName, cancellationToken);
                        _logger.LogInformation(
                            "POD successfully appended to SAP attachment {AttachmentEntry} for invoice {DocEntry}",
                            existingAttachmentEntry,
                            docEntry);
                    }
                    else
                    {
                        _logger.LogInformation("Uploading POD to SAP Attachments2 for invoice {DocEntry}...", docEntry);
                        var absEntry = await _sapClient.UploadAttachmentToSAPAsync(fileStream, fileName, cancellationToken);
                        _logger.LogInformation("SAP attachment created (AbsoluteEntry={AbsEntry}), linking to invoice {DocEntry}...", absEntry, docEntry);
                        await _sapClient.LinkAttachmentToInvoiceAsync(docEntry, absEntry, cancellationToken);
                        _logger.LogInformation("POD successfully synced to SAP for invoice {DocEntry} (AbsoluteEntry={AbsEntry})", docEntry, absEntry);
                    }
                }
            }
            else
            {
                _logger.LogWarning("Could not read local POD file for SAP sync (attachment {Id})", attachment.Id);
            }
        }
        catch (Exception ex)
        {
            // Non-blocking: SAP sync failure should never affect the local POD save
            _logger.LogWarning(ex, "Failed to sync POD to SAP for invoice {DocEntry} (non-blocking)", docEntry);
        }

        return CreatedAtAction(nameof(DownloadInvoiceAttachment),
            new { docEntry, attachmentId = attachment.Id }, attachment);
    }

    /// <summary>
    /// Get all POD attachments across all invoices (paginated).
    /// Optionally filter by customer code and date range.
    /// </summary>
    [HttpGet("pods")]
    [Authorize(Roles = "Admin,Cashier,PodOperator,Driver,SalesRep")]
    [ProducesResponseType(typeof(PodAttachmentListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllPods(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? cardCode = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        // Drivers can only see PODs they uploaded
        Guid? uploadedByUserId = null;
        if (User.IsInRole("Driver"))
        {
            uploadedByUserId = GetUserId();
        }

        var result = await _documentService.GetAllPodAttachmentsAsync(page, pageSize, cardCode, cancellationToken, fromDate, toDate, search, uploadedByUserId);
        return Ok(result);
    }

    /// <summary>
    /// Gets a report of invoices from SAP with their POD upload status.
    /// Excludes internal/intercompany card codes.
    /// </summary>
    [HttpGet("pod-upload-status")]
    [Authorize(Roles = "Admin,Cashier,PodOperator,Driver,SalesRep")]
    [ProducesResponseType(typeof(PodUploadStatusReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetPodUploadStatus(
        [FromQuery] DateTime fromDate,
        [FromQuery] DateTime toDate,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_settings.Enabled)
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });

            if (fromDate > toDate)
                return BadRequest(new ErrorResponseDto { Message = "From date cannot be later than to date" });

            // Lightweight query: only header fields, CardCode exclusion applied at SAP level
            var invoices = await _sapClient.GetInvoiceHeadersByDateRangeAsync(fromDate, toDate, ExcludedPodCardCodes.ToList(), cancellationToken);

            // Get all POD attachments for these invoices from local DB
            var docEntries = invoices.Select(i => i.DocEntry).ToList();
            var podLookup = await _documentService.GetPodStatusByDocEntriesAsync(docEntries, cancellationToken);

            var items = invoices.Select(i =>
            {
                podLookup.TryGetValue(i.DocEntry, out var podInfo);
                return new PodUploadStatusItemDto
                {
                    DocEntry = i.DocEntry,
                    DocNum = i.DocNum,
                    DocDate = i.DocDate,
                    CardCode = i.CardCode,
                    CardName = i.CardName,
                    DocTotal = i.DocTotal,
                    DocCurrency = i.DocCurrency,
                    HasPod = podInfo != null,
                    PodUploadedAt = podInfo?.UploadedAt,
                    PodUploadedBy = podInfo?.UploadedBy,
                    PodCount = podInfo?.Count ?? 0
                };
            }).OrderByDescending(i => i.DocNum).ToList();

            var report = new PodUploadStatusReportDto
            {
                FromDate = fromDate.ToString("yyyy-MM-dd"),
                ToDate = toDate.ToString("yyyy-MM-dd"),
                TotalInvoices = items.Count,
                UploadedCount = items.Count(i => i.HasPod),
                PendingCount = items.Count(i => !i.HasPod),
                Items = items
            };

            return Ok(report);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            return StatusCode(504, new ErrorResponseDto { Message = "Connection to SAP Service Layer timed out." });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(502, new ErrorResponseDto { Message = "Unable to connect to SAP Service Layer.", Errors = new List<string> { ex.Message } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating POD upload status report");
            return StatusCode(500, new ErrorResponseDto { Message = "Error generating report", Errors = new List<string> { ex.Message } });
        }
    }

    /// <summary>
    /// Get personal POD dashboard stats for the current user
    /// </summary>
    [HttpGet("pod-dashboard")]
    [Authorize(Roles = "Admin,Cashier,PodOperator,Driver,SalesRep")]
    [ProducesResponseType(typeof(PodDashboardDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetPodDashboard(CancellationToken cancellationToken = default)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        var dashboard = await _documentService.GetPodDashboardAsync(userId.Value, cancellationToken);
        return Ok(dashboard);
    }

    /// <summary>
    /// Gets invoices within a date range
    /// </summary>
    /// <param name="fromDate">Start date (yyyy-MM-dd)</param>
    /// <param name="toDate">End date (yyyy-MM-dd)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="page">Optional page number for paged results</param>
    /// <param name="pageSize">Optional page size for paged results</param>
    /// <returns>List of invoices</returns>
    [HttpGet("date-range")]
    [Authorize(Roles = "Admin,Cashier,StockController,DepotController,Manager")]
    [ProducesResponseType(typeof(InvoiceDateResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetInvoicesByDateRange(
        [FromQuery] DateTime fromDate,
        [FromQuery] DateTime toDate,
        CancellationToken cancellationToken = default,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null)
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

            var usePagination = page.HasValue || pageSize.HasValue;
            List<Invoice> invoices;
            int currentPage;
            int currentPageSize;
            int totalCount;
            int totalPages;
            bool hasMore;

            if (usePagination)
            {
                currentPage = Math.Max(page ?? 1, 1);
                currentPageSize = Math.Clamp(pageSize ?? 20, 1, 100);
                var skip = (currentPage - 1) * currentPageSize;

                invoices = await _sapClient.GetPagedInvoicesByOffsetAsync(skip, currentPageSize, null, null, fromDate, toDate, cancellationToken);
                totalCount = await _sapClient.GetInvoicesCountAsync(null, null, fromDate, toDate, cancellationToken);
                totalPages = currentPageSize > 0 ? (int)Math.Ceiling(totalCount / (double)currentPageSize) : 1;
                hasMore = (currentPage * currentPageSize) < totalCount;
            }
            else
            {
                invoices = await _sapClient.GetInvoicesByDateRangeAsync(fromDate, toDate, cancellationToken);
                currentPage = 1;
                currentPageSize = invoices.Count;
                totalCount = invoices.Count;
                totalPages = invoices.Count > 0 ? 1 : 0;
                hasMore = false;
            }

            _logger.LogInformation("Retrieved {Count} invoices between {FromDate} and {ToDate}",
                invoices.Count, fromDate.ToString("yyyy-MM-dd"), toDate.ToString("yyyy-MM-dd"));

            return Ok(new InvoiceDateResponseDto
            {
                FromDate = fromDate.ToString("yyyy-MM-dd"),
                ToDate = toDate.ToString("yyyy-MM-dd"),
                Page = currentPage,
                PageSize = currentPageSize,
                Count = invoices.Count,
                TotalCount = totalCount,
                TotalPages = totalPages,
                HasMore = hasMore,
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
    /// Gets invoices with pagination and optional filters
    /// </summary>
    /// <param name="page">Page number (default: 1)</param>
    /// <param name="pageSize">Number of records per page (default: 20, max: 100)</param>
    /// <param name="docNum">Optional document number filter</param>
    /// <param name="cardCode">Optional customer code filter</param>
    /// <param name="fromDate">Optional start date filter (yyyy-MM-dd)</param>
    /// <param name="toDate">Optional end date filter (yyyy-MM-dd)</param>
    /// <returns>List of invoices with pagination info</returns>
    [HttpGet("paged")]
    [Authorize(Roles = "Admin,Cashier,StockController,DepotController,Manager")]
    [ProducesResponseType(typeof(InvoiceListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetPagedInvoices(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] int? docNum = null,
        [FromQuery] string? cardCode = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
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

            var hasFilters = docNum.HasValue || !string.IsNullOrEmpty(cardCode) || fromDate.HasValue || toDate.HasValue;
            var maxPageSize = hasFilters ? 5000 : 100;

            if (pageSize < 1 || pageSize > maxPageSize)
            {
                return BadRequest(new ErrorResponseDto { Message = $"Page size must be between 1 and {maxPageSize}" });
            }

            var skip = (page - 1) * pageSize;
            var invoices = await _sapClient.GetPagedInvoicesByOffsetAsync(skip, pageSize, docNum, cardCode, fromDate, toDate, cancellationToken);
            var totalCount = await _sapClient.GetInvoicesCountAsync(docNum, cardCode, fromDate, toDate, cancellationToken);

            _logger.LogInformation("Retrieved page {Page} of invoices ({Count} records, total: {Total})",
                page, invoices.Count, totalCount);

            return Ok(new InvoiceListResponseDto
            {
                Page = page,
                PageSize = pageSize,
                Count = invoices.Count,
                TotalCount = totalCount,
                TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
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

            if (line.UnitPrice.HasValue && line.UnitPrice.Value <= 0)
            {
                errors.Add($"Line {i + 1} (Item: {line.ItemCode ?? "unknown"}): Unit price must be greater than zero. Current value: {line.UnitPrice.Value}");
            }
            else if (!line.UnitPrice.HasValue)
            {
                errors.Add($"Line {i + 1} (Item: {line.ItemCode ?? "unknown"}): Unit price is required and must be greater than zero.");
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

    private Guid? GetUserId()
    {
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(userIdClaim) && Guid.TryParse(userIdClaim, out var userId) && userId != Guid.Empty)
            return userId;

        return null;
    }

    #endregion
}
