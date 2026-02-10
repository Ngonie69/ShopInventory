using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;

namespace ShopInventory.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class InventoryTransferController : ControllerBase
{
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly IStockValidationService _stockValidation;
    private readonly SAPSettings _settings;
    private readonly ILogger<InventoryTransferController> _logger;

    public InventoryTransferController(
        ISAPServiceLayerClient sapClient,
        IStockValidationService stockValidation,
        IOptions<SAPSettings> settings,
        ILogger<InventoryTransferController> logger)
    {
        _sapClient = sapClient;
        _stockValidation = stockValidation;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new inventory transfer in SAP Business One
    /// </summary>
    /// <param name="request">The inventory transfer creation request</param>
    /// <returns>The created inventory transfer</returns>
    [HttpPost]
    [ProducesResponseType(typeof(InventoryTransferCreatedResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CreateInventoryTransfer(
        [FromBody] CreateInventoryTransferRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            // Validate the model
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

            // CRITICAL: Validate positive quantities
            var quantityErrors = ValidateTransferQuantities(request);
            if (quantityErrors.Count > 0)
            {
                _logger.LogWarning("Transfer quantity validation failed: {Errors}", string.Join(", ", quantityErrors));
                return BadRequest(new ErrorResponseDto
                {
                    Message = "Quantity validation failed - negative or zero quantities are not allowed",
                    Errors = quantityErrors
                });
            }

            // CRITICAL: Validate stock availability in source warehouse
            _logger.LogInformation("Validating stock availability for transfer with {LineCount} lines from {FromWarehouse} to {ToWarehouse}",
                request.Lines?.Count ?? 0, request.FromWarehouse, request.ToWarehouse);

            var stockValidationResult = await _stockValidation.ValidateInventoryTransferStockAsync(request, cancellationToken);
            if (!stockValidationResult.IsValid)
            {
                _logger.LogWarning("Stock validation failed for inventory transfer. {ErrorCount} items have insufficient stock",
                    stockValidationResult.Errors.Count);

                return BadRequest(new StockValidationResponseDto
                {
                    Message = "Insufficient stock in source warehouse - transfer would cause negative quantities",
                    IsValid = false,
                    Errors = stockValidationResult.Errors,
                    Warnings = stockValidationResult.Warnings,
                    Suggestions = stockValidationResult.Suggestions.Count > 0
                        ? stockValidationResult.Suggestions
                        : new List<string>
                        {
                            "Check stock levels in source warehouse using GET /api/Stock/{warehouseCode}",
                            "For batch-managed items, verify batch availability in source warehouse",
                            "Reduce transfer quantities to match available stock",
                            "Wait for pending purchase orders to be received"
                        }
                });
            }

            var transfer = await _sapClient.CreateInventoryTransferAsync(request, cancellationToken);

            _logger.LogInformation("Inventory transfer created successfully. DocEntry: {DocEntry}, DocNum: {DocNum}, From: {FromWarehouse}, To: {ToWarehouse}",
                transfer.DocEntry, transfer.DocNum, request.FromWarehouse, request.ToWarehouse);

            return CreatedAtAction(
                nameof(GetInventoryTransferByDocEntry),
                new { docEntry = transfer.DocEntry },
                new InventoryTransferCreatedResponseDto
                {
                    Message = "Inventory transfer created successfully",
                    Transfer = transfer.ToDto()
                });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Validation error creating inventory transfer");
            return BadRequest(new ErrorResponseDto { Message = "Validation error", Errors = ex.Message.Split("; ").ToList() });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("negative"))
        {
            _logger.LogError(ex, "CRITICAL: Attempted transfer would result in negative stock");
            return BadRequest(new ErrorResponseDto
            {
                Message = "Transfer rejected: Would result in negative stock quantities",
                Errors = new List<string> { ex.Message }
            });
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
            _logger.LogError(ex, "Error creating inventory transfer");
            return StatusCode(500, new ErrorResponseDto { Message = "Error creating inventory transfer", Errors = new List<string> { ex.Message } });
        }
    }

    /// <summary>
    /// Validates quantities in transfer request to ensure none are negative or zero
    /// </summary>
    private List<string> ValidateTransferQuantities(CreateInventoryTransferRequest request)
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

            // Validate batch quantities if present
            if (line.BatchNumbers != null)
            {
                decimal batchTotal = 0;
                for (int j = 0; j < line.BatchNumbers.Count; j++)
                {
                    var batch = line.BatchNumbers[j];
                    if (batch.Quantity <= 0)
                    {
                        errors.Add($"Line {i + 1}, Batch {j + 1} (Batch: {batch.BatchNumber ?? "unknown"}): Quantity must be greater than zero. Current value: {batch.Quantity}");
                    }
                    batchTotal += batch.Quantity;
                }

                // Warn if batch total doesn't match line quantity
                if (line.BatchNumbers.Count > 0 && Math.Abs(batchTotal - line.Quantity) > 0.0001m)
                {
                    errors.Add($"Line {i + 1} (Item: {line.ItemCode ?? "unknown"}): Batch quantities total ({batchTotal:N4}) does not match line quantity ({line.Quantity:N4})");
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// Gets all inventory transfers to a specific warehouse
    /// </summary>
    /// <param name="warehouseCode">The warehouse code to filter by</param>
    /// <returns>List of inventory transfers</returns>
    [HttpGet("{warehouseCode}")]
    [ProducesResponseType(typeof(InventoryTransferListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetInventoryTransfersByWarehouse(
        string warehouseCode,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            if (string.IsNullOrWhiteSpace(warehouseCode))
            {
                return BadRequest(new ErrorResponseDto { Message = "Warehouse code is required" });
            }

            var transfers = await _sapClient.GetInventoryTransfersToWarehouseAsync(
                warehouseCode,
                cancellationToken);

            _logger.LogInformation("Retrieved {Count} inventory transfers to warehouse {Warehouse}",
                transfers.Count, warehouseCode);

            return Ok(new InventoryTransferListResponseDto
            {
                Warehouse = warehouseCode,
                Count = transfers.Count,
                Transfers = transfers.ToDto()
            });
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
            _logger.LogError(ex, "Error retrieving inventory transfers for warehouse {Warehouse}", warehouseCode);
            return StatusCode(500, new ErrorResponseDto { Message = "Error retrieving inventory transfers", Errors = new List<string> { ex.Message } });
        }
    }

    /// <summary>
    /// Gets inventory transfers to a specific warehouse with pagination
    /// </summary>
    /// <param name="warehouseCode">The warehouse code to filter by</param>
    /// <param name="page">Page number (default: 1)</param>
    /// <param name="pageSize">Number of records per page (default: 20, max: 100)</param>
    /// <returns>List of inventory transfers with pagination info</returns>
    [HttpGet("{warehouseCode}/paged")]
    [ProducesResponseType(typeof(InventoryTransferListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetPagedInventoryTransfers(
        string warehouseCode,
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

            if (string.IsNullOrWhiteSpace(warehouseCode))
            {
                return BadRequest(new ErrorResponseDto { Message = "Warehouse code is required" });
            }

            // Validate pagination parameters
            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 20;
            if (pageSize > 100) pageSize = 100; // Limit max page size

            var transfers = await _sapClient.GetPagedInventoryTransfersToWarehouseAsync(
                warehouseCode,
                page,
                pageSize,
                cancellationToken);

            _logger.LogInformation("Retrieved {Count} inventory transfers (page {Page}) to warehouse {Warehouse}",
                transfers.Count, page, warehouseCode);

            return Ok(new InventoryTransferListResponseDto
            {
                Warehouse = warehouseCode,
                Page = page,
                PageSize = pageSize,
                Count = transfers.Count,
                HasMore = transfers.Count == pageSize,
                Transfers = transfers.ToDto()
            });
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
            _logger.LogError(ex, "Error retrieving inventory transfers for warehouse {Warehouse}", warehouseCode);
            return StatusCode(500, new ErrorResponseDto { Message = "Error retrieving inventory transfers", Errors = new List<string> { ex.Message } });
        }
    }

    /// <summary>
    /// Gets inventory transfers to a specific warehouse filtered by date
    /// </summary>
    /// <param name="warehouseCode">The warehouse code to filter by</param>
    /// <param name="date">The date to filter by (format: yyyy-MM-dd)</param>
    /// <returns>List of inventory transfers for the specified date</returns>
    [HttpGet("{warehouseCode}/date/{date}")]
    [ProducesResponseType(typeof(InventoryTransferDateResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetInventoryTransfersByDate(
        string warehouseCode,
        string date,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            if (string.IsNullOrWhiteSpace(warehouseCode))
            {
                return BadRequest(new ErrorResponseDto { Message = "Warehouse code is required" });
            }

            if (!DateTime.TryParse(date, out DateTime parsedDate))
            {
                return BadRequest(new ErrorResponseDto { Message = "Invalid date format. Use yyyy-MM-dd format." });
            }

            var transfers = await _sapClient.GetInventoryTransfersByDateAsync(
                warehouseCode,
                parsedDate,
                cancellationToken);

            _logger.LogInformation("Retrieved {Count} inventory transfers to warehouse {Warehouse} for date {Date}",
                transfers.Count, warehouseCode, date);

            return Ok(new InventoryTransferDateResponseDto
            {
                Warehouse = warehouseCode,
                Date = parsedDate.ToString("yyyy-MM-dd"),
                Count = transfers.Count,
                Transfers = transfers.ToDto()
            });
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
            _logger.LogError(ex, "Error retrieving inventory transfers for warehouse {Warehouse} on date {Date}", warehouseCode, date);
            return StatusCode(500, new ErrorResponseDto { Message = "Error retrieving inventory transfers", Errors = new List<string> { ex.Message } });
        }
    }

    /// <summary>
    /// Gets inventory transfers to a specific warehouse filtered by date range
    /// </summary>
    /// <param name="warehouseCode">The warehouse code to filter by</param>
    /// <param name="fromDate">Start date (format: yyyy-MM-dd)</param>
    /// <param name="toDate">End date (format: yyyy-MM-dd)</param>
    /// <returns>List of inventory transfers within the date range</returns>
    [HttpGet("{warehouseCode}/daterange")]
    [ProducesResponseType(typeof(InventoryTransferDateResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetInventoryTransfersByDateRange(
        string warehouseCode,
        [FromQuery] string fromDate,
        [FromQuery] string toDate,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            if (string.IsNullOrWhiteSpace(warehouseCode))
            {
                return BadRequest(new ErrorResponseDto { Message = "Warehouse code is required" });
            }

            if (!DateTime.TryParse(fromDate, out DateTime parsedFromDate))
            {
                return BadRequest(new ErrorResponseDto { Message = "Invalid fromDate format. Use yyyy-MM-dd format." });
            }

            if (!DateTime.TryParse(toDate, out DateTime parsedToDate))
            {
                return BadRequest(new ErrorResponseDto { Message = "Invalid toDate format. Use yyyy-MM-dd format." });
            }

            if (parsedFromDate > parsedToDate)
            {
                return BadRequest(new ErrorResponseDto { Message = "fromDate cannot be greater than toDate." });
            }

            var transfers = await _sapClient.GetInventoryTransfersByDateRangeAsync(
                warehouseCode,
                parsedFromDate,
                parsedToDate,
                cancellationToken);

            _logger.LogInformation("Retrieved {Count} inventory transfers to warehouse {Warehouse} from {FromDate} to {ToDate}",
                transfers.Count, warehouseCode, fromDate, toDate);

            return Ok(new InventoryTransferDateResponseDto
            {
                Warehouse = warehouseCode,
                FromDate = parsedFromDate.ToString("yyyy-MM-dd"),
                ToDate = parsedToDate.ToString("yyyy-MM-dd"),
                Count = transfers.Count,
                Transfers = transfers.ToDto()
            });
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
            _logger.LogError(ex, "Error retrieving inventory transfers for warehouse {Warehouse} from {FromDate} to {ToDate}", warehouseCode, fromDate, toDate);
            return StatusCode(500, new ErrorResponseDto { Message = "Error retrieving inventory transfers", Errors = new List<string> { ex.Message } });
        }
    }

    /// <summary>
    /// Gets a specific inventory transfer by document entry
    /// </summary>
    /// <param name="docEntry">The document entry ID</param>
    /// <returns>The inventory transfer details</returns>
    [HttpGet("detail/{docEntry:int}")]
    [ProducesResponseType(typeof(InventoryTransferDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetInventoryTransferByDocEntry(
        int docEntry,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            var transfer = await _sapClient.GetInventoryTransferByDocEntryAsync(docEntry, cancellationToken);

            if (transfer == null)
            {
                return NotFound(new ErrorResponseDto { Message = $"Inventory transfer with DocEntry {docEntry} not found" });
            }

            return Ok(transfer.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving inventory transfer {DocEntry}", docEntry);
            return StatusCode(500, new ErrorResponseDto { Message = "Error retrieving inventory transfer", Errors = new List<string> { ex.Message } });
        }
    }

    #region Transfer Request Endpoints

    /// <summary>
    /// Creates a new inventory transfer request in SAP Business One.
    /// Transfer requests are draft documents that require approval before becoming actual transfers.
    /// </summary>
    /// <param name="request">The transfer request creation data</param>
    /// <returns>The created transfer request</returns>
    [HttpPost("request")]
    [ProducesResponseType(typeof(TransferRequestCreatedResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CreateTransferRequest(
        [FromBody] CreateTransferRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            // Validate the model
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

            // Validate positive quantities
            var quantityErrors = ValidateTransferRequestQuantities(request);
            if (quantityErrors.Count > 0)
            {
                _logger.LogWarning("Transfer request quantity validation failed: {Errors}", string.Join(", ", quantityErrors));
                return BadRequest(new ErrorResponseDto
                {
                    Message = "Quantity validation failed - negative or zero quantities are not allowed",
                    Errors = quantityErrors
                });
            }

            _logger.LogInformation("Creating transfer request with {LineCount} lines from {FromWarehouse} to {ToWarehouse}",
                request.Lines?.Count ?? 0, request.FromWarehouse, request.ToWarehouse);

            var transferRequest = await _sapClient.CreateInventoryTransferRequestAsync(request, cancellationToken);

            _logger.LogInformation("Transfer request created successfully. DocEntry: {DocEntry}, DocNum: {DocNum}, From: {FromWarehouse}, To: {ToWarehouse}",
                transferRequest.DocEntry, transferRequest.DocNum, request.FromWarehouse, request.ToWarehouse);

            return CreatedAtAction(
                nameof(GetTransferRequestByDocEntry),
                new { docEntry = transferRequest.DocEntry },
                new TransferRequestCreatedResponseDto
                {
                    Message = "Transfer request created successfully",
                    TransferRequest = transferRequest.ToDto()
                });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Validation error creating transfer request");
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
            _logger.LogError(ex, "Error creating transfer request");
            return StatusCode(500, new ErrorResponseDto { Message = "Error creating transfer request", Errors = new List<string> { ex.Message } });
        }
    }

    /// <summary>
    /// Converts a transfer request to an actual inventory transfer
    /// </summary>
    /// <param name="docEntry">The document entry ID of the transfer request to convert</param>
    /// <returns>The created inventory transfer</returns>
    [HttpPost("request/{docEntry:int}/convert")]
    [ProducesResponseType(typeof(TransferRequestConvertedResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ConvertTransferRequestToTransfer(
        int docEntry,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            _logger.LogInformation("Converting transfer request {DocEntry} to inventory transfer", docEntry);

            var transfer = await _sapClient.ConvertTransferRequestToTransferAsync(docEntry, cancellationToken);

            _logger.LogInformation("Transfer request {DocEntry} converted successfully to transfer {TransferDocEntry}",
                docEntry, transfer.DocEntry);

            return CreatedAtAction(
                nameof(GetInventoryTransferByDocEntry),
                new { docEntry = transfer.DocEntry },
                new TransferRequestConvertedResponseDto
                {
                    Message = $"Transfer request converted successfully to Inventory Transfer #{transfer.DocNum}",
                    RequestDocEntry = docEntry,
                    Transfer = transfer.ToDto()
                });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Transfer request {DocEntry} not found", docEntry);
            return NotFound(new ErrorResponseDto { Message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Cannot convert transfer request {DocEntry}", docEntry);
            return BadRequest(new ErrorResponseDto { Message = ex.Message });
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
            _logger.LogError(ex, "Error converting transfer request {DocEntry}", docEntry);
            return StatusCode(500, new ErrorResponseDto { Message = "Error converting transfer request", Errors = new List<string> { ex.Message } });
        }
    }

    /// <summary>
    /// Gets a specific transfer request by document entry
    /// </summary>
    /// <param name="docEntry">The document entry ID</param>
    /// <returns>The transfer request details</returns>
    [HttpGet("request/{docEntry:int}")]
    [ProducesResponseType(typeof(InventoryTransferRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetTransferRequestByDocEntry(
        int docEntry,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            var transferRequest = await _sapClient.GetInventoryTransferRequestByDocEntryAsync(docEntry, cancellationToken);

            if (transferRequest == null)
            {
                return NotFound(new ErrorResponseDto { Message = $"Transfer request with DocEntry {docEntry} not found" });
            }

            return Ok(transferRequest.ToDto());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving transfer request {DocEntry}", docEntry);
            return StatusCode(500, new ErrorResponseDto { Message = "Error retrieving transfer request", Errors = new List<string> { ex.Message } });
        }
    }

    /// <summary>
    /// Gets all transfer requests to a specific warehouse
    /// </summary>
    /// <param name="warehouseCode">The warehouse code to filter by</param>
    /// <returns>List of transfer requests</returns>
    [HttpGet("requests/{warehouseCode}")]
    [ProducesResponseType(typeof(TransferRequestListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetTransferRequestsByWarehouse(
        string warehouseCode,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            if (string.IsNullOrWhiteSpace(warehouseCode))
            {
                return BadRequest(new ErrorResponseDto { Message = "Warehouse code is required" });
            }

            var transferRequests = await _sapClient.GetInventoryTransferRequestsByWarehouseAsync(
                warehouseCode,
                cancellationToken);

            _logger.LogInformation("Retrieved {Count} transfer requests to warehouse {Warehouse}",
                transferRequests.Count, warehouseCode);

            return Ok(new TransferRequestListResponseDto
            {
                Warehouse = warehouseCode,
                Count = transferRequests.Count,
                TransferRequests = transferRequests.ToDto()
            });
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
            _logger.LogError(ex, "Error retrieving transfer requests for warehouse {Warehouse}", warehouseCode);
            return StatusCode(500, new ErrorResponseDto { Message = "Error retrieving transfer requests", Errors = new List<string> { ex.Message } });
        }
    }

    /// <summary>
    /// Gets transfer requests with pagination
    /// </summary>
    /// <param name="page">Page number (default: 1)</param>
    /// <param name="pageSize">Number of records per page (default: 20, max: 100)</param>
    /// <returns>List of transfer requests with pagination info</returns>
    [HttpGet("requests")]
    [ProducesResponseType(typeof(TransferRequestListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetPagedTransferRequests(
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

            // Validate pagination parameters
            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 20;
            if (pageSize > 100) pageSize = 100; // Limit max page size

            var transferRequests = await _sapClient.GetPagedInventoryTransferRequestsAsync(
                page,
                pageSize,
                cancellationToken);

            _logger.LogInformation("Retrieved {Count} transfer requests (page {Page})",
                transferRequests.Count, page);

            return Ok(new TransferRequestListResponseDto
            {
                Page = page,
                PageSize = pageSize,
                Count = transferRequests.Count,
                HasMore = transferRequests.Count == pageSize,
                TransferRequests = transferRequests.ToDto()
            });
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
            _logger.LogError(ex, "Error retrieving transfer requests");
            return StatusCode(500, new ErrorResponseDto { Message = "Error retrieving transfer requests", Errors = new List<string> { ex.Message } });
        }
    }

    /// <summary>
    /// Validates quantities in transfer request to ensure none are negative or zero
    /// </summary>
    private List<string> ValidateTransferRequestQuantities(CreateTransferRequestDto request)
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
        }

        return errors;
    }

    #endregion
}
