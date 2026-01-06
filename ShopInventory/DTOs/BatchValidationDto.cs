using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

/// <summary>
/// Request for validating and auto-allocating batches for an invoice
/// </summary>
public class BatchAllocationRequest
{
    /// <summary>
    /// The invoice lines requiring batch allocation
    /// </summary>
    [Required]
    public List<BatchAllocationLineRequest> Lines { get; set; } = new();

    /// <summary>
    /// Whether to auto-allocate batches using FIFO/FEFO when not explicitly specified
    /// </summary>
    public bool AutoAllocate { get; set; } = true;

    /// <summary>
    /// The allocation strategy to use: FEFO (First Expired First Out) or FIFO (First In First Out)
    /// FEFO is preferred when expiry dates exist
    /// </summary>
    public BatchAllocationStrategy Strategy { get; set; } = BatchAllocationStrategy.FEFO;
}

/// <summary>
/// Line item for batch allocation request
/// </summary>
public class BatchAllocationLineRequest
{
    /// <summary>
    /// Line number in the invoice (1-based)
    /// </summary>
    public int LineNumber { get; set; }

    /// <summary>
    /// Item code
    /// </summary>
    [Required]
    public string ItemCode { get; set; } = string.Empty;

    /// <summary>
    /// Warehouse code (required for batch validation)
    /// </summary>
    [Required]
    public string WarehouseCode { get; set; } = string.Empty;

    /// <summary>
    /// Quantity required (in the UoM specified)
    /// </summary>
    [Required]
    [Range(0.00001, double.MaxValue)]
    public decimal Quantity { get; set; }

    /// <summary>
    /// Unit of Measure code for the quantity
    /// </summary>
    public string? UoMCode { get; set; }

    /// <summary>
    /// UoM Entry (SAP UoM identifier)
    /// </summary>
    public int? UoMEntry { get; set; }

    /// <summary>
    /// Explicitly specified batch allocations (optional if auto-allocation is enabled)
    /// </summary>
    public List<BatchQuantityRequest>? BatchAllocations { get; set; }
}

/// <summary>
/// Individual batch quantity allocation
/// </summary>
public class BatchQuantityRequest
{
    /// <summary>
    /// The batch number
    /// </summary>
    [Required]
    public string BatchNumber { get; set; } = string.Empty;

    /// <summary>
    /// Quantity from this batch (in sales UoM or inventory UoM based on context)
    /// </summary>
    [Required]
    [Range(0.00001, double.MaxValue)]
    public decimal Quantity { get; set; }
}

/// <summary>
/// Batch allocation strategy
/// </summary>
public enum BatchAllocationStrategy
{
    /// <summary>
    /// First Expired First Out - prioritize batches with nearest expiry date
    /// </summary>
    FEFO = 0,

    /// <summary>
    /// First In First Out - prioritize batches by admission date
    /// </summary>
    FIFO = 1,

    /// <summary>
    /// Manual only - require explicit batch allocations, no auto-allocation
    /// </summary>
    Manual = 2
}

/// <summary>
/// Result of batch allocation validation
/// </summary>
public class BatchAllocationResult
{
    /// <summary>
    /// Whether the allocation is valid and can proceed
    /// </summary>
    public bool IsValid => ValidationErrors.Count == 0;

    /// <summary>
    /// Validation errors preventing the allocation
    /// </summary>
    public List<BatchValidationErrorDto> ValidationErrors { get; set; } = new();

    /// <summary>
    /// Warnings that don't prevent allocation but should be noted
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// Successfully allocated batches (populated after validation)
    /// </summary>
    public List<AllocatedBatchLine> AllocatedLines { get; set; } = new();

    /// <summary>
    /// Suggestions for resolving validation issues
    /// </summary>
    public List<string> Suggestions { get; set; } = new();

    /// <summary>
    /// Total items validated
    /// </summary>
    public int TotalLinesValidated { get; set; }

    /// <summary>
    /// Items that passed validation
    /// </summary>
    public int LinesPassedValidation { get; set; }

    /// <summary>
    /// Whether batches were auto-allocated
    /// </summary>
    public bool BatchesAutoAllocated { get; set; }

    /// <summary>
    /// The allocation strategy used
    /// </summary>
    public BatchAllocationStrategy StrategyUsed { get; set; }

    /// <summary>
    /// Timestamp of validation
    /// </summary>
    public DateTime ValidatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Creates a successful result
    /// </summary>
    public static BatchAllocationResult Success(List<AllocatedBatchLine> allocatedLines, BatchAllocationStrategy strategy)
    {
        return new BatchAllocationResult
        {
            AllocatedLines = allocatedLines,
            TotalLinesValidated = allocatedLines.Count,
            LinesPassedValidation = allocatedLines.Count,
            StrategyUsed = strategy,
            BatchesAutoAllocated = true
        };
    }

    /// <summary>
    /// Creates a failure result
    /// </summary>
    public static BatchAllocationResult Failure(List<BatchValidationErrorDto> errors, List<string>? suggestions = null)
    {
        return new BatchAllocationResult
        {
            ValidationErrors = errors,
            Suggestions = suggestions ?? new List<string>()
        };
    }
}

/// <summary>
/// Represents an allocated batch line after successful validation
/// </summary>
public class AllocatedBatchLine
{
    /// <summary>
    /// Line number in the original request (1-based)
    /// </summary>
    public int LineNumber { get; set; }

    /// <summary>
    /// Item code
    /// </summary>
    public string ItemCode { get; set; } = string.Empty;

    /// <summary>
    /// Warehouse code
    /// </summary>
    public string WarehouseCode { get; set; } = string.Empty;

    /// <summary>
    /// Total quantity allocated (in inventory UoM)
    /// </summary>
    public decimal TotalQuantityAllocated { get; set; }

    /// <summary>
    /// Quantity in sales/requested UoM
    /// </summary>
    public decimal OriginalRequestedQuantity { get; set; }

    /// <summary>
    /// UoM conversion factor applied (if any)
    /// </summary>
    public decimal UoMConversionFactor { get; set; } = 1.0m;

    /// <summary>
    /// Individual batch allocations
    /// </summary>
    public List<AllocatedBatch> Batches { get; set; } = new();
}

/// <summary>
/// Individual batch allocation details
/// </summary>
public class AllocatedBatch
{
    /// <summary>
    /// Batch number
    /// </summary>
    public string BatchNumber { get; set; } = string.Empty;

    /// <summary>
    /// Quantity allocated from this batch (in inventory UoM)
    /// </summary>
    public decimal QuantityAllocated { get; set; }

    /// <summary>
    /// Available quantity before allocation
    /// </summary>
    public decimal AvailableBeforeAllocation { get; set; }

    /// <summary>
    /// Remaining quantity after allocation
    /// </summary>
    public decimal RemainingAfterAllocation { get; set; }

    /// <summary>
    /// Batch expiry date (used for FEFO sorting)
    /// </summary>
    public DateTime? ExpiryDate { get; set; }

    /// <summary>
    /// Batch admission date (used for FIFO sorting)
    /// </summary>
    public DateTime? AdmissionDate { get; set; }

    /// <summary>
    /// Manufacturing date
    /// </summary>
    public DateTime? ManufacturingDate { get; set; }

    /// <summary>
    /// Order in which this batch was selected (for audit)
    /// </summary>
    public int AllocationOrder { get; set; }
}

/// <summary>
/// Detailed batch validation error with actionable information
/// </summary>
public class BatchValidationErrorDto
{
    /// <summary>
    /// Error code for programmatic handling
    /// </summary>
    public BatchValidationErrorCode ErrorCode { get; set; }

    /// <summary>
    /// Line number where the error occurred (1-based)
    /// </summary>
    public int LineNumber { get; set; }

    /// <summary>
    /// Item code
    /// </summary>
    public string ItemCode { get; set; } = string.Empty;

    /// <summary>
    /// Item name/description
    /// </summary>
    public string? ItemName { get; set; }

    /// <summary>
    /// Warehouse code
    /// </summary>
    public string WarehouseCode { get; set; } = string.Empty;

    /// <summary>
    /// Batch number (if error is batch-specific)
    /// </summary>
    public string? BatchNumber { get; set; }

    /// <summary>
    /// Quantity requested
    /// </summary>
    public decimal RequestedQuantity { get; set; }

    /// <summary>
    /// Quantity available
    /// </summary>
    public decimal AvailableQuantity { get; set; }

    /// <summary>
    /// Shortage amount (requested - available)
    /// </summary>
    public decimal Shortage => Math.Max(0, RequestedQuantity - AvailableQuantity);

    /// <summary>
    /// UoM for the quantities
    /// </summary>
    public string? UnitOfMeasure { get; set; }

    /// <summary>
    /// Human-readable error message
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Suggested action to resolve this error
    /// </summary>
    public string SuggestedAction { get; set; } = string.Empty;

    /// <summary>
    /// Alternative batches available (if any)
    /// </summary>
    public List<AvailableBatchDto>? AlternativeBatches { get; set; }
}

/// <summary>
/// Available batch information for alternatives
/// </summary>
public class AvailableBatchDto
{
    /// <summary>
    /// Batch number
    /// </summary>
    public string BatchNumber { get; set; } = string.Empty;

    /// <summary>
    /// Available quantity in this batch
    /// </summary>
    public decimal AvailableQuantity { get; set; }

    /// <summary>
    /// Expiry date
    /// </summary>
    public DateTime? ExpiryDate { get; set; }

    /// <summary>
    /// Admission date
    /// </summary>
    public DateTime? AdmissionDate { get; set; }

    /// <summary>
    /// Whether this batch is recommended based on FIFO/FEFO
    /// </summary>
    public bool IsRecommended { get; set; }
}

/// <summary>
/// Error codes for batch validation failures
/// </summary>
public enum BatchValidationErrorCode
{
    /// <summary>
    /// Batch allocation is required but not provided
    /// </summary>
    BatchAllocationRequired = 1,

    /// <summary>
    /// Sum of batch quantities doesn't match line quantity
    /// </summary>
    BatchQuantityMismatch = 2,

    /// <summary>
    /// Specific batch has insufficient quantity
    /// </summary>
    InsufficientBatchQuantity = 3,

    /// <summary>
    /// Batch not found in the specified warehouse
    /// </summary>
    BatchNotFound = 4,

    /// <summary>
    /// Warehouse code is missing
    /// </summary>
    WarehouseRequired = 5,

    /// <summary>
    /// Item not found
    /// </summary>
    ItemNotFound = 6,

    /// <summary>
    /// Total available across all batches is insufficient
    /// </summary>
    InsufficientTotalStock = 7,

    /// <summary>
    /// UoM conversion error
    /// </summary>
    UoMConversionError = 8,

    /// <summary>
    /// Concurrency conflict - stock was modified during validation
    /// </summary>
    ConcurrencyConflict = 9,

    /// <summary>
    /// Item is not batch-managed but batch allocation was provided
    /// </summary>
    ItemNotBatchManaged = 10,

    /// <summary>
    /// Quantity must be positive
    /// </summary>
    InvalidQuantity = 11,

    /// <summary>
    /// Batch has expired
    /// </summary>
    BatchExpired = 12,

    /// <summary>
    /// Lock could not be acquired - try again
    /// </summary>
    LockAcquisitionFailed = 13
}

/// <summary>
/// Response for stock validation with batch details
/// </summary>
public class BatchStockValidationResponseDto
{
    /// <summary>
    /// Overall validation message
    /// </summary>
    public string Message { get; set; } = "Stock validation failed";

    /// <summary>
    /// Whether validation passed
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// Detailed validation errors
    /// </summary>
    public List<BatchValidationErrorDto> Errors { get; set; } = new();

    /// <summary>
    /// Non-critical warnings
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// Suggestions for resolving issues
    /// </summary>
    public List<string> Suggestions { get; set; } = new();

    /// <summary>
    /// Allocated batches (if validation succeeded)
    /// </summary>
    public List<AllocatedBatchLine>? AllocatedBatches { get; set; }

    /// <summary>
    /// Lock token for use in subsequent POST (if validation succeeded)
    /// </summary>
    public string? LockToken { get; set; }

    /// <summary>
    /// Lock expiry time
    /// </summary>
    public DateTime? LockExpiresAt { get; set; }
}

/// <summary>
/// UoM information for conversion
/// </summary>
public class UoMInfoDto
{
    /// <summary>
    /// UoM code (e.g., "KG", "PC", "BOX")
    /// </summary>
    public string UoMCode { get; set; } = string.Empty;

    /// <summary>
    /// SAP UoM Entry
    /// </summary>
    public int? UoMEntry { get; set; }

    /// <summary>
    /// Conversion factor to inventory UoM (e.g., 1 BOX = 12 PC, factor = 12)
    /// </summary>
    public decimal ConversionFactor { get; set; } = 1.0m;

    /// <summary>
    /// Base/inventory UoM code
    /// </summary>
    public string InventoryUoMCode { get; set; } = string.Empty;
}
