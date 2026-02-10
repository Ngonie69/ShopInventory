using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Services;

/// <summary>
/// Interface for getting reserved quantities (to break circular dependency)
/// </summary>
public interface IReservedQuantityProvider
{
    /// <summary>
    /// Gets the total reserved quantity for an item in a warehouse.
    /// </summary>
    Task<decimal> GetReservedQuantityAsync(string itemCode, string warehouseCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the reserved quantity for a specific batch.
    /// </summary>
    Task<decimal> GetReservedBatchQuantityAsync(string itemCode, string warehouseCode, string batchNumber, CancellationToken cancellationToken = default);
}

/// <summary>
/// Service for batch-level inventory validation and auto-allocation for SAP B1 invoicing.
/// Implements FIFO/FEFO strategies and prevents negative batch quantities.
/// </summary>
public interface IBatchInventoryValidationService
{
    /// <summary>
    /// Validates and optionally auto-allocates batches for invoice lines.
    /// This is the main entry point for batch validation before posting to SAP.
    /// </summary>
    /// <param name="request">The invoice request to validate</param>
    /// <param name="autoAllocate">Whether to auto-allocate batches using FIFO/FEFO</param>
    /// <param name="strategy">Allocation strategy (FEFO or FIFO)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Validation result with allocated batches</returns>
    Task<BatchAllocationResult> ValidateAndAllocateBatchesAsync(
        CreateInvoiceRequest request,
        bool autoAllocate = true,
        BatchAllocationStrategy strategy = BatchAllocationStrategy.FEFO,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that batch allocations match line quantities (with UoM conversion).
    /// </summary>
    Task<List<BatchValidationErrorDto>> ValidateBatchQuantityMatchAsync(
        CreateInvoiceRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets available batches for an item in a warehouse, sorted by FIFO/FEFO.
    /// </summary>
    Task<List<AvailableBatchDto>> GetAvailableBatchesAsync(
        string itemCode,
        string warehouseCode,
        BatchAllocationStrategy strategy = BatchAllocationStrategy.FEFO,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs final pre-post validation with locking.
    /// This should be called immediately before posting to SAP.
    /// </summary>
    Task<BatchStockValidationResponseDto> PrePostValidationAsync(
        CreateInvoiceRequest request,
        List<AllocatedBatchLine>? previouslyAllocatedBatches = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets UoM conversion factor for an item.
    /// </summary>
    Task<UoMInfoDto?> GetUoMConversionAsync(
        string itemCode,
        string? uomCode,
        int? uomEntry,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if an item is batch-managed.
    /// </summary>
    Task<bool> IsBatchManagedItemAsync(
        string itemCode,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementation of batch inventory validation service
/// </summary>
public class BatchInventoryValidationService : IBatchInventoryValidationService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly IInventoryLockService _lockService;
    private readonly ILogger<BatchInventoryValidationService> _logger;
    private IReservedQuantityProvider? _reservedQuantityProvider;

    // Cache for batch-managed status
    private static readonly Dictionary<string, bool> _batchManagedCache = new();
    private static readonly object _cacheLock = new();

    public BatchInventoryValidationService(
        ApplicationDbContext dbContext,
        ISAPServiceLayerClient sapClient,
        IInventoryLockService lockService,
        ILogger<BatchInventoryValidationService> logger)
    {
        _dbContext = dbContext;
        _sapClient = sapClient;
        _lockService = lockService;
        _logger = logger;
    }

    /// <summary>
    /// Sets the reserved quantity provider (called by DI after construction to avoid circular dependency)
    /// </summary>
    public void SetReservedQuantityProvider(IReservedQuantityProvider provider)
    {
        _reservedQuantityProvider = provider;
    }

    /// <summary>
    /// Gets the reserved quantity for an item, using the provider if available
    /// </summary>
    private async Task<decimal> GetReservedQuantityInternalAsync(
        string itemCode, string warehouseCode, CancellationToken cancellationToken)
    {
        if (_reservedQuantityProvider == null)
            return 0;

        return await _reservedQuantityProvider.GetReservedQuantityAsync(itemCode, warehouseCode, cancellationToken);
    }

    /// <summary>
    /// Gets the reserved batch quantity for an item, using the provider if available
    /// </summary>
    private async Task<decimal> GetReservedBatchQuantityInternalAsync(
        string itemCode, string warehouseCode, string batchNumber, CancellationToken cancellationToken)
    {
        if (_reservedQuantityProvider == null)
            return 0;

        return await _reservedQuantityProvider.GetReservedBatchQuantityAsync(itemCode, warehouseCode, batchNumber, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<BatchAllocationResult> ValidateAndAllocateBatchesAsync(
        CreateInvoiceRequest request,
        bool autoAllocate = true,
        BatchAllocationStrategy strategy = BatchAllocationStrategy.FEFO,
        CancellationToken cancellationToken = default)
    {
        var result = new BatchAllocationResult
        {
            StrategyUsed = strategy,
            BatchesAutoAllocated = autoAllocate
        };

        if (request.Lines == null || request.Lines.Count == 0)
        {
            return result;
        }

        // Use parallel processing for validating all lines concurrently
        var validationTasks = request.Lines.Select((line, index) =>
            ValidateLineAsync(line, index + 1, autoAllocate, strategy, cancellationToken));

        var lineResults = await Task.WhenAll(validationTasks);

        // Aggregate results from all parallel validations
        var allocatedLines = new List<AllocatedBatchLine>();
        var errors = new List<BatchValidationErrorDto>();
        var warnings = new List<string>();

        foreach (var lineResult in lineResults.OrderBy(r => r.LineNumber))
        {
            errors.AddRange(lineResult.Errors);
            warnings.AddRange(lineResult.Warnings);
            if (lineResult.AllocatedLine != null)
            {
                allocatedLines.Add(lineResult.AllocatedLine);
            }
        }

        result.TotalLinesValidated = request.Lines.Count;
        result.LinesPassedValidation = allocatedLines.Count;
        result.ValidationErrors = errors;
        result.Warnings = warnings;
        result.AllocatedLines = allocatedLines;

        if (errors.Count > 0)
        {
            result.Suggestions = GenerateSuggestions(errors);
        }

        return result;
    }

    /// <summary>
    /// Validates a single invoice line - called in parallel for each line
    /// </summary>
    private async Task<LineValidationResult> ValidateLineAsync(
        CreateInvoiceLineRequest line,
        int lineNumber,
        bool autoAllocate,
        BatchAllocationStrategy strategy,
        CancellationToken cancellationToken)
    {
        var result = new LineValidationResult { LineNumber = lineNumber };

        // Rule 5: Require explicit WarehouseCode
        if (string.IsNullOrWhiteSpace(line.WarehouseCode))
        {
            result.Errors.Add(CreateError(
                BatchValidationErrorCode.WarehouseRequired,
                lineNumber,
                line.ItemCode ?? "",
                null,
                "",
                line.Quantity,
                0,
                "Warehouse code is required for each invoice line",
                "Specify a warehouse code for this line"));
            return result;
        }

        // Check if item is batch-managed
        var isBatchManaged = await IsBatchManagedItemAsync(line.ItemCode ?? "", cancellationToken);

        if (!isBatchManaged)
        {
            // Non-batch item - just validate stock availability
            var stockCheck = await ValidateNonBatchItemStockAsync(
                line.ItemCode ?? "",
                line.WarehouseCode,
                line.Quantity,
                line.UoMCode,
                lineNumber,
                cancellationToken);

            if (stockCheck.error != null)
            {
                result.Errors.Add(stockCheck.error);
            }
            else
            {
                result.AllocatedLine = new AllocatedBatchLine
                {
                    LineNumber = lineNumber,
                    ItemCode = line.ItemCode ?? "",
                    WarehouseCode = line.WarehouseCode,
                    OriginalRequestedQuantity = line.Quantity,
                    TotalQuantityAllocated = stockCheck.inventoryQuantity,
                    UoMConversionFactor = stockCheck.conversionFactor,
                    Batches = new List<AllocatedBatch>()
                };
            }
            return result;
        }

        // Rule 1: For batch-managed items, BatchNumbers must be present or auto-allocated
        if (line.BatchNumbers == null || line.BatchNumbers.Count == 0)
        {
            if (!autoAllocate || strategy == BatchAllocationStrategy.Manual)
            {
                result.Errors.Add(CreateError(
                    BatchValidationErrorCode.BatchAllocationRequired,
                    lineNumber,
                    line.ItemCode ?? "",
                    null,
                    line.WarehouseCode,
                    line.Quantity,
                    0,
                    "Batch allocation is required for batch-managed items",
                    "Specify batch numbers or enable auto-allocation"));
                return result;
            }

            // Auto-allocate batches using FIFO/FEFO
            var autoAllocationResult = await AutoAllocateBatchesAsync(
                line.ItemCode ?? "",
                line.WarehouseCode,
                line.Quantity,
                line.UoMCode,
                strategy,
                lineNumber,
                cancellationToken);

            if (autoAllocationResult.error != null)
            {
                result.Errors.Add(autoAllocationResult.error);
            }
            else if (autoAllocationResult.allocatedLine != null)
            {
                result.AllocatedLine = autoAllocationResult.allocatedLine;
            }
        }
        else
        {
            // Rule 2: Validate explicit batch allocations
            var validationResult = await ValidateExplicitBatchAllocationsAsync(
                line,
                lineNumber,
                cancellationToken);

            result.Errors.AddRange(validationResult.errors);
            result.Warnings.AddRange(validationResult.warnings);

            if (validationResult.allocatedLine != null && validationResult.errors.Count == 0)
            {
                result.AllocatedLine = validationResult.allocatedLine;
            }
        }

        return result;
    }

    /// <summary>
    /// Helper class to hold individual line validation results for parallel processing
    /// </summary>
    private class LineValidationResult
    {
        public int LineNumber { get; set; }
        public List<BatchValidationErrorDto> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
        public AllocatedBatchLine? AllocatedLine { get; set; }
    }

    /// <inheritdoc/>
    public async Task<List<BatchValidationErrorDto>> ValidateBatchQuantityMatchAsync(
        CreateInvoiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<BatchValidationErrorDto>();

        if (request.Lines == null)
            return errors;

        for (int i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            var lineNumber = i + 1;

            if (line.BatchNumbers == null || line.BatchNumbers.Count == 0)
                continue;

            // Get UoM conversion
            var conversionFactor = 1.0m;
            if (!string.IsNullOrEmpty(line.UoMCode))
            {
                var uomInfo = await GetUoMConversionAsync(
                    line.ItemCode ?? "",
                    line.UoMCode,
                    null,
                    cancellationToken);

                if (uomInfo != null)
                {
                    conversionFactor = uomInfo.ConversionFactor;
                }
            }

            // Calculate expected inventory quantity
            var expectedInventoryQty = line.Quantity * conversionFactor;
            var batchTotal = line.BatchNumbers.Sum(b => b.Quantity);

            // Rule 1: Sum of batch quantities must equal line quantity (in inventory UoM)
            if (Math.Abs(batchTotal - expectedInventoryQty) > 0.0001m)
            {
                errors.Add(CreateError(
                    BatchValidationErrorCode.BatchQuantityMismatch,
                    lineNumber,
                    line.ItemCode ?? "",
                    null,
                    line.WarehouseCode ?? "",
                    expectedInventoryQty,
                    batchTotal,
                    $"Batch quantities total ({batchTotal:N4}) does not match line quantity ({expectedInventoryQty:N4} in inventory UoM)",
                    $"Adjust batch quantities to total {expectedInventoryQty:N4}"));
            }
        }

        return errors;
    }

    /// <inheritdoc/>
    public async Task<List<AvailableBatchDto>> GetAvailableBatchesAsync(
        string itemCode,
        string warehouseCode,
        BatchAllocationStrategy strategy = BatchAllocationStrategy.FEFO,
        CancellationToken cancellationToken = default)
    {
        // Try local database first
        var localBatches = await _dbContext.ProductBatches
            .Include(b => b.Product)
            .Where(b => b.Product.ItemCode == itemCode
                     && b.WarehouseCode == warehouseCode
                     && b.IsActive
                     && b.Quantity > 0)
            .ToListAsync(cancellationToken);

        List<BatchNumber>? sapBatches = null;

        // If no local data or stale, fetch from SAP
        if (localBatches.Count == 0)
        {
            try
            {
                sapBatches = await _sapClient.GetBatchNumbersForItemInWarehouseAsync(
                    itemCode, warehouseCode, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch batches from SAP for {ItemCode} in {Warehouse}",
                    itemCode, warehouseCode);
            }
        }

        var result = new List<AvailableBatchDto>();

        if (sapBatches != null && sapBatches.Count > 0)
        {
            result = sapBatches
                .Where(b => b.Quantity > 0)
                .Select(b => new AvailableBatchDto
                {
                    BatchNumber = b.BatchNum ?? "",
                    AvailableQuantity = b.Quantity,
                    ExpiryDate = ParseDate(b.ExpiryDate),
                    AdmissionDate = ParseDate(b.AdmissionDate)
                })
                .ToList();
        }
        else if (localBatches.Count > 0)
        {
            result = localBatches
                .Select(b => new AvailableBatchDto
                {
                    BatchNumber = b.BatchNumber,
                    AvailableQuantity = b.Quantity,
                    ExpiryDate = b.ExpiryDate,
                    AdmissionDate = b.AdmissionDate
                })
                .ToList();
        }

        // Apply FIFO/FEFO sorting
        result = ApplySortingStrategy(result, strategy);

        // Mark recommended batches
        var runningTotal = 0m;
        foreach (var batch in result)
        {
            batch.IsRecommended = runningTotal < 1000000; // Mark first batches as recommended
            runningTotal += batch.AvailableQuantity;
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task<BatchStockValidationResponseDto> PrePostValidationAsync(
        CreateInvoiceRequest request,
        List<AllocatedBatchLine>? previouslyAllocatedBatches = null,
        CancellationToken cancellationToken = default)
    {
        var response = new BatchStockValidationResponseDto();

        if (request.Lines == null || request.Lines.Count == 0)
        {
            response.IsValid = true;
            response.Message = "No lines to validate";
            return response;
        }

        // Build lock requests for all items being invoiced
        var lockRequests = new List<InventoryLockRequest>();
        foreach (var line in request.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.ItemCode) || string.IsNullOrWhiteSpace(line.WarehouseCode))
                continue;

            // Lock at item+warehouse level
            lockRequests.Add(new InventoryLockRequest
            {
                ItemCode = line.ItemCode,
                WarehouseCode = line.WarehouseCode
            });

            // Also lock specific batches if specified
            if (line.BatchNumbers != null)
            {
                foreach (var batch in line.BatchNumbers)
                {
                    if (!string.IsNullOrEmpty(batch.BatchNumber))
                    {
                        lockRequests.Add(new InventoryLockRequest
                        {
                            ItemCode = line.ItemCode,
                            WarehouseCode = line.WarehouseCode,
                            BatchNumber = batch.BatchNumber
                        });
                    }
                }
            }
        }

        // Deduplicate lock requests
        lockRequests = lockRequests
            .GroupBy(r => r.ToLockKey())
            .Select(g => g.First())
            .ToList();

        // Rule 4: Acquire locks to prevent concurrent consumption
        var lockResult = await _lockService.TryAcquireMultipleLocksAsync(
            lockRequests,
            TimeSpan.FromSeconds(30),
            cancellationToken);

        if (!lockResult.AllAcquired)
        {
            response.IsValid = false;
            response.Message = "Could not acquire inventory locks - concurrent access detected";
            response.Errors = lockResult.FailedLocks.Select(f => CreateError(
                BatchValidationErrorCode.LockAcquisitionFailed,
                0,
                f.ItemCode,
                null,
                f.WarehouseCode,
                0,
                0,
                f.Reason,
                f.RetryAfter.HasValue
                    ? $"Retry after {f.RetryAfter.Value.TotalSeconds:N0} seconds"
                    : "Please try again")).ToList();

            response.Suggestions = new List<string>
            {
                "Another user may be processing an invoice for the same items",
                "Please wait a moment and try again",
                "If the problem persists, check for stuck transactions"
            };

            return response;
        }

        try
        {
            // Re-validate stock with locks held
            var validationResult = await ValidateAndAllocateBatchesAsync(
                request,
                autoAllocate: true,
                BatchAllocationStrategy.FEFO,
                cancellationToken);

            if (!validationResult.IsValid)
            {
                response.IsValid = false;
                response.Message = "Stock validation failed during pre-post check";
                response.Errors = validationResult.ValidationErrors;
                response.Warnings = validationResult.Warnings;
                response.Suggestions = validationResult.Suggestions;
                return response;
            }

            // Compare with previously allocated batches if provided
            if (previouslyAllocatedBatches != null)
            {
                var discrepancies = CompareAllocations(previouslyAllocatedBatches, validationResult.AllocatedLines);
                if (discrepancies.Count > 0)
                {
                    response.Warnings.AddRange(discrepancies);
                    _logger.LogWarning("Stock changed between initial validation and pre-post: {Discrepancies}",
                        string.Join("; ", discrepancies));
                }
            }

            response.IsValid = true;
            response.Message = "Stock validation successful";
            response.AllocatedBatches = validationResult.AllocatedLines;
            response.LockToken = lockResult.CombinedLockToken;
            response.LockExpiresAt = lockResult.EarliestExpiry;

            // Note: Locks will be held until invoice is posted or lock expires
            // Caller should release locks after successful SAP post or on error
        }
        catch
        {
            // Release locks on error
            if (lockResult.LockTokens.Count > 0)
            {
                await _lockService.ReleaseMultipleLocksAsync(lockResult.LockTokens);
            }
            throw;
        }

        return response;
    }

    /// <inheritdoc/>
    public async Task<UoMInfoDto?> GetUoMConversionAsync(
        string itemCode,
        string? uomCode,
        int? uomEntry,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(uomCode) && !uomEntry.HasValue)
        {
            return null;
        }

        // Try to get from local database
        var product = await _dbContext.Products
            .FirstOrDefaultAsync(p => p.ItemCode == itemCode, cancellationToken);

        if (product == null)
        {
            // Try to get from SAP
            try
            {
                var item = await _sapClient.GetItemByCodeAsync(itemCode, cancellationToken);
                if (item != null)
                {
                    var inventoryUoM = item.InventoryUOM ?? "PC";

                    // If the requested UoM is the same as inventory UoM, factor is 1
                    if (string.Equals(uomCode, inventoryUoM, StringComparison.OrdinalIgnoreCase))
                    {
                        return new UoMInfoDto
                        {
                            UoMCode = uomCode ?? inventoryUoM,
                            InventoryUoMCode = inventoryUoM,
                            ConversionFactor = 1.0m
                        };
                    }

                    // TODO: Query SAP for UoM group conversion factors
                    // For now, return 1:1 conversion
                    return new UoMInfoDto
                    {
                        UoMCode = uomCode ?? inventoryUoM,
                        InventoryUoMCode = inventoryUoM,
                        ConversionFactor = 1.0m
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get UoM info from SAP for {ItemCode}", itemCode);
            }
        }

        // Default: assume 1:1 conversion
        return new UoMInfoDto
        {
            UoMCode = uomCode ?? "PC",
            InventoryUoMCode = product?.InventoryUOM ?? "PC",
            ConversionFactor = 1.0m
        };
    }

    /// <inheritdoc/>
    public async Task<bool> IsBatchManagedItemAsync(
        string itemCode,
        CancellationToken cancellationToken = default)
    {
        // Check cache first
        lock (_cacheLock)
        {
            if (_batchManagedCache.TryGetValue(itemCode, out var cached))
            {
                return cached;
            }
        }

        // Check local database
        var product = await _dbContext.Products
            .FirstOrDefaultAsync(p => p.ItemCode == itemCode, cancellationToken);

        if (product != null)
        {
            lock (_cacheLock)
            {
                _batchManagedCache[itemCode] = product.ManageBatchNumbers;
            }
            return product.ManageBatchNumbers;
        }

        // Check SAP
        try
        {
            var item = await _sapClient.GetItemByCodeAsync(itemCode, cancellationToken);
            if (item != null)
            {
                var isBatchManaged = item.ManageBatchNumbers == "tYES" ||
                                     item.ManageBatchNumbers == "Y" ||
                                     item.ManageBatchNumbers == "true";

                lock (_cacheLock)
                {
                    _batchManagedCache[itemCode] = isBatchManaged;
                }
                return isBatchManaged;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check if {ItemCode} is batch-managed", itemCode);
        }

        return false; // Assume not batch-managed if we can't determine
    }

    #region Private Helper Methods

    private async Task<(BatchValidationErrorDto? error, AllocatedBatchLine? allocatedLine)> AutoAllocateBatchesAsync(
        string itemCode,
        string warehouseCode,
        decimal requestedQuantity,
        string? uomCode,
        BatchAllocationStrategy strategy,
        int lineNumber,
        CancellationToken cancellationToken)
    {
        // Get UoM conversion
        var uomInfo = await GetUoMConversionAsync(itemCode, uomCode, null, cancellationToken);
        var conversionFactor = uomInfo?.ConversionFactor ?? 1.0m;
        var inventoryQuantityNeeded = requestedQuantity * conversionFactor;

        // Get available batches sorted by strategy
        var availableBatches = await GetAvailableBatchesAsync(
            itemCode, warehouseCode, strategy, cancellationToken);

        if (availableBatches.Count == 0)
        {
            return (CreateError(
                BatchValidationErrorCode.InsufficientTotalStock,
                lineNumber,
                itemCode,
                null,
                warehouseCode,
                inventoryQuantityNeeded,
                0,
                $"No batches available for item {itemCode} in warehouse {warehouseCode}",
                "Check stock or transfer inventory to this warehouse"), null);
        }

        // Adjust available quantities by subtracting reserved quantities
        var effectiveAvailableBatches = new List<(AvailableBatchDto batch, decimal effectiveQty)>();
        foreach (var batch in availableBatches)
        {
            var reservedQty = await GetReservedBatchQuantityInternalAsync(
                itemCode, warehouseCode, batch.BatchNumber ?? "", cancellationToken);
            var effectiveQty = batch.AvailableQuantity - reservedQty;
            if (effectiveQty > 0)
            {
                effectiveAvailableBatches.Add((batch, effectiveQty));
            }
        }

        var totalEffectiveAvailable = effectiveAvailableBatches.Sum(b => b.effectiveQty);
        if (totalEffectiveAvailable < inventoryQuantityNeeded)
        {
            var totalPhysical = availableBatches.Sum(b => b.AvailableQuantity);
            var totalReserved = totalPhysical - totalEffectiveAvailable;
            var message = totalReserved > 0
                ? $"Insufficient stock. Need {inventoryQuantityNeeded:N4}, available {totalEffectiveAvailable:N4} (Physical: {totalPhysical:N4}, Reserved: {totalReserved:N4})"
                : $"Insufficient total stock. Need {inventoryQuantityNeeded:N4}, available {totalEffectiveAvailable:N4}";

            return (CreateError(
                BatchValidationErrorCode.InsufficientTotalStock,
                lineNumber,
                itemCode,
                null,
                warehouseCode,
                inventoryQuantityNeeded,
                totalEffectiveAvailable,
                message,
                $"Reduce quantity to {totalEffectiveAvailable:N4} or transfer more stock",
                availableBatches), null);
        }

        // Allocate batches using FIFO/FEFO (using effective available quantities)
        var allocatedBatches = new List<AllocatedBatch>();
        var remainingQty = inventoryQuantityNeeded;
        var allocationOrder = 1;

        foreach (var (batch, effectiveQty) in effectiveAvailableBatches)
        {
            if (remainingQty <= 0)
                break;

            var allocateFromBatch = Math.Min(effectiveQty, remainingQty);

            allocatedBatches.Add(new AllocatedBatch
            {
                BatchNumber = batch.BatchNumber,
                QuantityAllocated = allocateFromBatch,
                AvailableBeforeAllocation = effectiveQty,
                RemainingAfterAllocation = effectiveQty - allocateFromBatch,
                ExpiryDate = batch.ExpiryDate,
                AdmissionDate = batch.AdmissionDate,
                AllocationOrder = allocationOrder++
            });

            remainingQty -= allocateFromBatch;
        }

        var allocatedLine = new AllocatedBatchLine
        {
            LineNumber = lineNumber,
            ItemCode = itemCode,
            WarehouseCode = warehouseCode,
            OriginalRequestedQuantity = requestedQuantity,
            TotalQuantityAllocated = inventoryQuantityNeeded,
            UoMConversionFactor = conversionFactor,
            Batches = allocatedBatches
        };

        _logger.LogInformation(
            "Auto-allocated {BatchCount} batches for {ItemCode} using {Strategy}: {Batches}",
            allocatedBatches.Count,
            itemCode,
            strategy,
            string.Join(", ", allocatedBatches.Select(b => $"{b.BatchNumber}:{b.QuantityAllocated:N2}")));

        return (null, allocatedLine);
    }

    private async Task<(List<BatchValidationErrorDto> errors, List<string> warnings, AllocatedBatchLine? allocatedLine)>
        ValidateExplicitBatchAllocationsAsync(
            CreateInvoiceLineRequest line,
            int lineNumber,
            CancellationToken cancellationToken)
    {
        var errors = new List<BatchValidationErrorDto>();
        var warnings = new List<string>();

        // Get UoM conversion
        var uomInfo = await GetUoMConversionAsync(
            line.ItemCode ?? "", line.UoMCode, null, cancellationToken);
        var conversionFactor = uomInfo?.ConversionFactor ?? 1.0m;
        var expectedInventoryQty = line.Quantity * conversionFactor;

        // Validate batch total matches line quantity
        var batchTotal = line.BatchNumbers!.Sum(b => b.Quantity);
        if (Math.Abs(batchTotal - expectedInventoryQty) > 0.0001m)
        {
            errors.Add(CreateError(
                BatchValidationErrorCode.BatchQuantityMismatch,
                lineNumber,
                line.ItemCode ?? "",
                null,
                line.WarehouseCode ?? "",
                expectedInventoryQty,
                batchTotal,
                $"Batch quantities total ({batchTotal:N4}) does not match line quantity ({expectedInventoryQty:N4})",
                $"Adjust batch quantities to total {expectedInventoryQty:N4}"));
        }

        // Get available batches for validation
        var availableBatches = await GetAvailableBatchesAsync(
            line.ItemCode ?? "",
            line.WarehouseCode ?? "",
            BatchAllocationStrategy.FEFO,
            cancellationToken);

        var batchLookup = availableBatches.ToDictionary(
            b => b.BatchNumber,
            StringComparer.OrdinalIgnoreCase);

        var allocatedBatches = new List<AllocatedBatch>();
        var allocationOrder = 1;

        foreach (var batchRequest in line.BatchNumbers!)
        {
            // Validate quantity is positive
            if (batchRequest.Quantity <= 0)
            {
                errors.Add(CreateError(
                    BatchValidationErrorCode.InvalidQuantity,
                    lineNumber,
                    line.ItemCode ?? "",
                    batchRequest.BatchNumber,
                    line.WarehouseCode ?? "",
                    0,
                    batchRequest.Quantity,
                    "Batch quantity must be greater than zero",
                    "Specify a positive quantity"));
                continue;
            }

            // Check batch exists and has sufficient quantity
            if (!batchLookup.TryGetValue(batchRequest.BatchNumber ?? "", out var availableBatch))
            {
                errors.Add(CreateError(
                    BatchValidationErrorCode.BatchNotFound,
                    lineNumber,
                    line.ItemCode ?? "",
                    batchRequest.BatchNumber,
                    line.WarehouseCode ?? "",
                    batchRequest.Quantity,
                    0,
                    $"Batch '{batchRequest.BatchNumber}' not found in warehouse '{line.WarehouseCode}'",
                    "Select a valid batch from available batches",
                    availableBatches));
                continue;
            }

            if (batchRequest.Quantity > availableBatch.AvailableQuantity)
            {
                errors.Add(CreateError(
                    BatchValidationErrorCode.InsufficientBatchQuantity,
                    lineNumber,
                    line.ItemCode ?? "",
                    batchRequest.BatchNumber,
                    line.WarehouseCode ?? "",
                    batchRequest.Quantity,
                    availableBatch.AvailableQuantity,
                    $"Insufficient quantity in batch '{batchRequest.BatchNumber}'. Requested: {batchRequest.Quantity:N4}, Available: {availableBatch.AvailableQuantity:N4}",
                    $"Reduce quantity to {availableBatch.AvailableQuantity:N4} or use multiple batches",
                    availableBatches));
                continue;
            }

            // Check for expired batches
            if (availableBatch.ExpiryDate.HasValue && availableBatch.ExpiryDate.Value < DateTime.Today)
            {
                warnings.Add($"Line {lineNumber}: Batch '{batchRequest.BatchNumber}' expired on {availableBatch.ExpiryDate:yyyy-MM-dd}");
            }

            allocatedBatches.Add(new AllocatedBatch
            {
                BatchNumber = batchRequest.BatchNumber ?? "",
                QuantityAllocated = batchRequest.Quantity,
                AvailableBeforeAllocation = availableBatch.AvailableQuantity,
                RemainingAfterAllocation = availableBatch.AvailableQuantity - batchRequest.Quantity,
                ExpiryDate = availableBatch.ExpiryDate,
                AdmissionDate = availableBatch.AdmissionDate,
                AllocationOrder = allocationOrder++
            });
        }

        if (errors.Count > 0)
        {
            return (errors, warnings, null);
        }

        var allocatedLine = new AllocatedBatchLine
        {
            LineNumber = lineNumber,
            ItemCode = line.ItemCode ?? "",
            WarehouseCode = line.WarehouseCode ?? "",
            OriginalRequestedQuantity = line.Quantity,
            TotalQuantityAllocated = expectedInventoryQty,
            UoMConversionFactor = conversionFactor,
            Batches = allocatedBatches
        };

        return (errors, warnings, allocatedLine);
    }

    private async Task<(BatchValidationErrorDto? error, decimal inventoryQuantity, decimal conversionFactor)>
        ValidateNonBatchItemStockAsync(
            string itemCode,
            string warehouseCode,
            decimal requestedQuantity,
            string? uomCode,
            int lineNumber,
            CancellationToken cancellationToken)
    {
        // Get UoM conversion
        var uomInfo = await GetUoMConversionAsync(itemCode, uomCode, null, cancellationToken);
        var conversionFactor = uomInfo?.ConversionFactor ?? 1.0m;
        var inventoryQuantityNeeded = requestedQuantity * conversionFactor;

        // Get available quantity from SAP
        try
        {
            var stockQuantities = await _sapClient.GetStockQuantitiesInWarehouseAsync(
                warehouseCode, cancellationToken);

            var stock = stockQuantities?.FirstOrDefault(s =>
                string.Equals(s.ItemCode, itemCode, StringComparison.OrdinalIgnoreCase));

            if (stock == null)
            {
                return (CreateError(
                    BatchValidationErrorCode.ItemNotFound,
                    lineNumber,
                    itemCode,
                    null,
                    warehouseCode,
                    inventoryQuantityNeeded,
                    0,
                    $"Item '{itemCode}' not found in warehouse '{warehouseCode}'",
                    "Check item code and warehouse"), 0, 1);
            }

            // Account for reserved quantities from pending reservations
            var reservedQty = await GetReservedQuantityInternalAsync(itemCode, warehouseCode, cancellationToken);
            var effectiveAvailable = stock.Available - reservedQty;

            if (inventoryQuantityNeeded > effectiveAvailable)
            {
                var message = reservedQty > 0
                    ? $"Insufficient stock. Requested: {inventoryQuantityNeeded:N4}, Available: {effectiveAvailable:N4} (Physical: {stock.Available:N4}, Reserved: {reservedQty:N4})"
                    : $"Insufficient stock. Requested: {inventoryQuantityNeeded:N4}, Available: {stock.Available:N4}";

                return (CreateError(
                    BatchValidationErrorCode.InsufficientTotalStock,
                    lineNumber,
                    itemCode,
                    null,
                    warehouseCode,
                    inventoryQuantityNeeded,
                    effectiveAvailable,
                    message,
                    $"Reduce quantity to {effectiveAvailable:N4}"), 0, 1);
            }

            return (null, inventoryQuantityNeeded, conversionFactor);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to validate stock for non-batch item {ItemCode}", itemCode);
            return (null, inventoryQuantityNeeded, conversionFactor); // Allow to proceed, SAP will validate
        }
    }

    private List<AvailableBatchDto> ApplySortingStrategy(
        List<AvailableBatchDto> batches,
        BatchAllocationStrategy strategy)
    {
        return strategy switch
        {
            BatchAllocationStrategy.FEFO => batches
                .OrderBy(b => b.ExpiryDate ?? DateTime.MaxValue)
                .ThenBy(b => b.AdmissionDate ?? DateTime.MaxValue)
                .ToList(),

            BatchAllocationStrategy.FIFO => batches
                .OrderBy(b => b.AdmissionDate ?? DateTime.MaxValue)
                .ThenBy(b => b.ExpiryDate ?? DateTime.MaxValue)
                .ToList(),

            _ => batches
        };
    }

    private static List<string> CompareAllocations(
        List<AllocatedBatchLine> previous,
        List<AllocatedBatchLine> current)
    {
        var discrepancies = new List<string>();

        foreach (var prevLine in previous)
        {
            var currLine = current.FirstOrDefault(c =>
                c.LineNumber == prevLine.LineNumber &&
                c.ItemCode == prevLine.ItemCode);

            if (currLine == null)
            {
                discrepancies.Add($"Line {prevLine.LineNumber} ({prevLine.ItemCode}): No longer valid");
                continue;
            }

            foreach (var prevBatch in prevLine.Batches)
            {
                var currBatch = currLine.Batches.FirstOrDefault(b =>
                    b.BatchNumber == prevBatch.BatchNumber);

                if (currBatch == null)
                {
                    discrepancies.Add(
                        $"Line {prevLine.LineNumber}: Batch {prevBatch.BatchNumber} no longer available");
                }
                else if (currBatch.AvailableBeforeAllocation < prevBatch.AvailableBeforeAllocation)
                {
                    discrepancies.Add(
                        $"Line {prevLine.LineNumber}: Batch {prevBatch.BatchNumber} stock reduced from {prevBatch.AvailableBeforeAllocation:N2} to {currBatch.AvailableBeforeAllocation:N2}");
                }
            }
        }

        return discrepancies;
    }

    private static List<string> GenerateSuggestions(List<BatchValidationErrorDto> errors)
    {
        var suggestions = new HashSet<string>();

        foreach (var error in errors)
        {
            switch (error.ErrorCode)
            {
                case BatchValidationErrorCode.BatchAllocationRequired:
                    suggestions.Add("Enable auto-allocation or specify batch numbers for batch-managed items");
                    break;

                case BatchValidationErrorCode.InsufficientBatchQuantity:
                case BatchValidationErrorCode.InsufficientTotalStock:
                    suggestions.Add("Check stock levels using GET /api/Stock/{warehouseCode}");
                    suggestions.Add("Use GET /api/Product/{itemCode}/batches/{warehouseCode} to see available batches");
                    suggestions.Add("Consider requesting an inventory transfer to replenish stock");
                    break;

                case BatchValidationErrorCode.BatchNotFound:
                    suggestions.Add("Verify batch numbers are correct");
                    suggestions.Add("Check if batches are in the correct warehouse");
                    break;

                case BatchValidationErrorCode.WarehouseRequired:
                    suggestions.Add("Specify warehouse code for each invoice line");
                    break;

                case BatchValidationErrorCode.LockAcquisitionFailed:
                    suggestions.Add("Wait a moment and retry - another transaction may be in progress");
                    break;

                case BatchValidationErrorCode.BatchQuantityMismatch:
                    suggestions.Add("Ensure batch quantities sum to the line quantity (in inventory UoM)");
                    break;
            }
        }

        return suggestions.ToList();
    }

    private static BatchValidationErrorDto CreateError(
        BatchValidationErrorCode errorCode,
        int lineNumber,
        string itemCode,
        string? itemName,
        string warehouseCode,
        decimal requestedQuantity,
        decimal availableQuantity,
        string message,
        string suggestedAction,
        List<AvailableBatchDto>? alternativeBatches = null)
    {
        return new BatchValidationErrorDto
        {
            ErrorCode = errorCode,
            LineNumber = lineNumber,
            ItemCode = itemCode,
            ItemName = itemName,
            WarehouseCode = warehouseCode,
            RequestedQuantity = requestedQuantity,
            AvailableQuantity = availableQuantity,
            Message = message,
            SuggestedAction = suggestedAction,
            AlternativeBatches = alternativeBatches?.Take(5).ToList()
        };
    }

    private static DateTime? ParseDate(string? dateString)
    {
        if (string.IsNullOrEmpty(dateString))
            return null;

        if (DateTime.TryParse(dateString, out var date))
            return date;

        return null;
    }

    #endregion
}
