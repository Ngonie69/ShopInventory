using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace ShopInventory.Services;

/// <summary>
/// Service for validating stock availability and preventing negative quantities.
/// This is a CRITICAL service for preventing stock variances.
/// </summary>
public interface IStockValidationService
{
    /// <summary>
    /// Validates that sufficient stock is available for an invoice.
    /// Returns validation errors if stock is insufficient.
    /// </summary>
    Task<StockValidationResult> ValidateInvoiceStockAsync(
        CreateInvoiceRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that sufficient stock is available for an inventory transfer.
    /// Returns validation errors if stock is insufficient.
    /// </summary>
    Task<StockValidationResult> ValidateInventoryTransferStockAsync(
        CreateInventoryTransferRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates quantities are positive (not zero or negative).
    /// </summary>
    List<string> ValidatePositiveQuantities(IEnumerable<QuantityValidationItem> items);

    /// <summary>
    /// Checks if a product batch has sufficient quantity for the requested amount.
    /// </summary>
    Task<bool> HasSufficientBatchQuantityAsync(
        string itemCode,
        string batchNumber,
        string warehouseCode,
        decimal requestedQuantity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the available quantity for an item in a specific warehouse.
    /// </summary>
    Task<decimal> GetAvailableQuantityAsync(
        string itemCode,
        string warehouseCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates local stock after a successful SAP transaction.
    /// </summary>
    Task UpdateLocalStockAsync(
        string itemCode,
        string warehouseCode,
        decimal quantityChange,
        string transactionType,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of stock validation
/// </summary>
public class StockValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public List<StockValidationError> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> Suggestions { get; set; } = new();

    public static StockValidationResult Success() => new();

    public static StockValidationResult Failure(List<StockValidationError> errors)
    {
        return new StockValidationResult { Errors = errors };
    }
}

/// <summary>
/// Item for quantity validation
/// </summary>
public class QuantityValidationItem
{
    public int LineNumber { get; set; }
    public string? ItemCode { get; set; }
    public decimal Quantity { get; set; }
    public string? FieldName { get; set; }
}

/// <summary>
/// Request for creating an inventory transfer (for validation)
/// </summary>
public class CreateInventoryTransferRequest
{
    /// <summary>
    /// Source warehouse code (can be overridden per line)
    /// </summary>
    public string? FromWarehouse { get; set; }

    /// <summary>
    /// Destination warehouse code (required)
    /// </summary>
    [Required(ErrorMessage = "Destination warehouse is required")]
    public string? ToWarehouse { get; set; }

    /// <summary>
    /// Optional document date (defaults to today)
    /// </summary>
    public string? DocDate { get; set; }

    /// <summary>
    /// Optional due date
    /// </summary>
    public string? DueDate { get; set; }

    /// <summary>
    /// Optional comments/notes
    /// </summary>
    public string? Comments { get; set; }

    /// <summary>
    /// Transfer line items (at least one required)
    /// </summary>
    [Required(ErrorMessage = "At least one line item is required")]
    [MinLength(1, ErrorMessage = "At least one line item is required")]
    public List<CreateInventoryTransferLineRequest>? Lines { get; set; }
}

/// <summary>
/// Line item for inventory transfer request
/// </summary>
public class CreateInventoryTransferLineRequest
{
    /// <summary>
    /// Item code (required)
    /// </summary>
    [Required(ErrorMessage = "Item code is required")]
    public string? ItemCode { get; set; }

    /// <summary>
    /// Quantity to transfer (must be positive)
    /// </summary>
    [Required(ErrorMessage = "Quantity is required")]
    [Range(0.00001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero")]
    public decimal Quantity { get; set; }

    /// <summary>
    /// Source warehouse for this line (overrides header FromWarehouse)
    /// </summary>
    public string? FromWarehouseCode { get; set; }

    /// <summary>
    /// Destination warehouse for this line (overrides header ToWarehouse)
    /// </summary>
    public string? ToWarehouseCode { get; set; }

    /// <summary>
    /// Batch allocations for batch-managed items
    /// </summary>
    public List<TransferBatchRequest>? BatchNumbers { get; set; }
}

/// <summary>
/// Batch allocation for transfer
/// </summary>
public class TransferBatchRequest
{
    /// <summary>
    /// Batch number (required)
    /// </summary>
    [Required(ErrorMessage = "Batch number is required")]
    public string? BatchNumber { get; set; }

    /// <summary>
    /// Quantity from this batch (must be positive)
    /// </summary>
    [Required(ErrorMessage = "Quantity is required")]
    [Range(0.00001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero")]
    public decimal Quantity { get; set; }
}

/// <summary>
/// Request for creating an incoming payment
/// </summary>
public class CreateIncomingPaymentRequest
{
    /// <summary>
    /// Customer card code (required)
    /// </summary>
    [Required(ErrorMessage = "Customer code is required")]
    public string? CardCode { get; set; }

    /// <summary>
    /// Document date (defaults to today)
    /// </summary>
    public string? DocDate { get; set; }

    /// <summary>
    /// Optional remarks
    /// </summary>
    public string? Remarks { get; set; }

    /// <summary>
    /// Cash payment amount (non-negative)
    /// </summary>
    [Range(0, double.MaxValue, ErrorMessage = "Cash sum cannot be negative")]
    public decimal CashSum { get; set; }

    /// <summary>
    /// Transfer payment amount (non-negative)
    /// </summary>
    [Range(0, double.MaxValue, ErrorMessage = "Transfer sum cannot be negative")]
    public decimal TransferSum { get; set; }

    /// <summary>
    /// Transfer reference number
    /// </summary>
    public string? TransferReference { get; set; }

    /// <summary>
    /// Transfer date
    /// </summary>
    public string? TransferDate { get; set; }

    /// <summary>
    /// Transfer account
    /// </summary>
    public string? TransferAccount { get; set; }

    /// <summary>
    /// Check payment amount (non-negative)
    /// </summary>
    [Range(0, double.MaxValue, ErrorMessage = "Check sum cannot be negative")]
    public decimal CheckSum { get; set; }

    /// <summary>
    /// Credit card payment amount (non-negative)
    /// </summary>
    [Range(0, double.MaxValue, ErrorMessage = "Credit sum cannot be negative")]
    public decimal CreditSum { get; set; }

    /// <summary>
    /// Invoices to apply this payment to
    /// </summary>
    public List<PaymentInvoiceRequest>? PaymentInvoices { get; set; }

    /// <summary>
    /// Check details (if paying by check)
    /// </summary>
    public List<PaymentCheckRequest>? PaymentChecks { get; set; }

    /// <summary>
    /// Credit card details (if paying by credit card)
    /// </summary>
    public List<PaymentCreditCardRequest>? PaymentCreditCards { get; set; }
}

/// <summary>
/// Invoice to apply payment to
/// </summary>
public class PaymentInvoiceRequest
{
    /// <summary>
    /// Invoice DocEntry
    /// </summary>
    [Required(ErrorMessage = "Invoice DocEntry is required")]
    public int DocEntry { get; set; }

    /// <summary>
    /// Amount to apply to this invoice (non-negative)
    /// </summary>
    [Range(0, double.MaxValue, ErrorMessage = "Sum applied cannot be negative")]
    public decimal SumApplied { get; set; }

    /// <summary>
    /// Invoice type (default: it_Invoice)
    /// </summary>
    public string? InvoiceType { get; set; }
}

/// <summary>
/// Check payment details
/// </summary>
public class PaymentCheckRequest
{
    /// <summary>
    /// Check due date
    /// </summary>
    public string? DueDate { get; set; }

    /// <summary>
    /// Check number
    /// </summary>
    [Required(ErrorMessage = "Check number is required")]
    public int CheckNumber { get; set; }

    /// <summary>
    /// Bank code
    /// </summary>
    public string? BankCode { get; set; }

    /// <summary>
    /// Branch
    /// </summary>
    public string? Branch { get; set; }

    /// <summary>
    /// Account number
    /// </summary>
    public string? AccountNum { get; set; }

    /// <summary>
    /// Check amount (non-negative)
    /// </summary>
    [Range(0, double.MaxValue, ErrorMessage = "Check sum cannot be negative")]
    public decimal CheckSum { get; set; }
}

/// <summary>
/// Credit card payment details
/// </summary>
public class PaymentCreditCardRequest
{
    /// <summary>
    /// Credit card type
    /// </summary>
    public int CreditCard { get; set; }

    /// <summary>
    /// Card number (last 4 digits)
    /// </summary>
    public string? CreditCardNumber { get; set; }

    /// <summary>
    /// Card valid until date
    /// </summary>
    public string? CardValidUntil { get; set; }

    /// <summary>
    /// Voucher number
    /// </summary>
    public string? VoucherNum { get; set; }

    /// <summary>
    /// Credit amount (non-negative)
    /// </summary>
    [Range(0, double.MaxValue, ErrorMessage = "Credit sum cannot be negative")]
    public decimal CreditSum { get; set; }
}

/// <summary>
/// Implementation of stock validation service
/// </summary>
public class StockValidationService : IStockValidationService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly ILogger<StockValidationService> _logger;

    public StockValidationService(
        ApplicationDbContext dbContext,
        ISAPServiceLayerClient sapClient,
        ILogger<StockValidationService> logger)
    {
        _dbContext = dbContext;
        _sapClient = sapClient;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<StockValidationResult> ValidateInvoiceStockAsync(
        CreateInvoiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = new StockValidationResult();

        if (request.Lines == null || request.Lines.Count == 0)
        {
            return result; // No lines to validate
        }

        // First, validate all quantities are positive
        var quantityItems = request.Lines.Select((line, index) => new QuantityValidationItem
        {
            LineNumber = index + 1,
            ItemCode = line.ItemCode,
            Quantity = line.Quantity,
            FieldName = "Quantity"
        }).ToList();

        var quantityErrors = ValidatePositiveQuantities(quantityItems);
        if (quantityErrors.Count > 0)
        {
            foreach (var error in quantityErrors)
            {
                result.Errors.Add(new StockValidationError
                {
                    LineNumber = 0,
                    ItemCode = "",
                    WarehouseCode = "",
                    RequestedQuantity = 0,
                    AvailableQuantity = 0
                });
            }
            result.Suggestions.Add("All quantities must be greater than zero");
            return result;
        }

        // Validate batch quantities if specified
        for (int i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            if (line.BatchNumbers != null && line.BatchNumbers.Count > 0)
            {
                var batchItems = line.BatchNumbers.Select((b, idx) => new QuantityValidationItem
                {
                    LineNumber = i + 1,
                    ItemCode = line.ItemCode,
                    Quantity = b.Quantity,
                    FieldName = $"Batch '{b.BatchNumber}' Quantity"
                });

                var batchErrors = ValidatePositiveQuantities(batchItems);
                if (batchErrors.Count > 0)
                {
                    result.Suggestions.AddRange(batchErrors);
                }

                // Validate batch total matches line quantity
                var batchTotal = line.BatchNumbers.Sum(b => b.Quantity);
                if (Math.Abs(batchTotal - line.Quantity) > 0.0001m)
                {
                    result.Warnings.Add(
                        $"Line {i + 1} (Item: {line.ItemCode}): Batch quantities total ({batchTotal:N4}) " +
                        $"does not match line quantity ({line.Quantity:N4})");
                }
            }
        }

        // Use SAP client for comprehensive stock validation
        try
        {
            var sapErrors = await _sapClient.ValidateStockAvailabilityAsync(request, cancellationToken);
            if (sapErrors.Count > 0)
            {
                result.Errors.AddRange(sapErrors);
                result.Suggestions.Add("Check stock levels using GET /api/Stock/{warehouseCode}");
                result.Suggestions.Add("For batch-managed items, verify batch availability");
                result.Suggestions.Add("Consider splitting the order across multiple batches");
                result.Suggestions.Add("Request an inventory transfer to replenish stock");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to validate stock with SAP, proceeding with local validation only");
            result.Warnings.Add("Could not validate stock with SAP - SAP will reject if insufficient stock");
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task<StockValidationResult> ValidateInventoryTransferStockAsync(
        CreateInventoryTransferRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = new StockValidationResult();

        if (request.Lines == null || request.Lines.Count == 0)
        {
            return result;
        }

        // Validate positive quantities
        var quantityItems = request.Lines.Select((line, index) => new QuantityValidationItem
        {
            LineNumber = index + 1,
            ItemCode = line.ItemCode,
            Quantity = line.Quantity,
            FieldName = "Quantity"
        }).ToList();

        var quantityErrors = ValidatePositiveQuantities(quantityItems);
        if (quantityErrors.Count > 0)
        {
            result.Suggestions.AddRange(quantityErrors);
            result.Suggestions.Add("All transfer quantities must be greater than zero");
            return result;
        }

        // Validate source warehouse has sufficient stock
        for (int i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            var fromWarehouse = line.FromWarehouseCode ?? request.FromWarehouse ?? "01";
            var itemCode = line.ItemCode ?? "";

            if (string.IsNullOrEmpty(itemCode))
            {
                result.Errors.Add(new StockValidationError
                {
                    LineNumber = i + 1,
                    ItemCode = "",
                    WarehouseCode = fromWarehouse,
                    RequestedQuantity = line.Quantity,
                    AvailableQuantity = 0
                });
                continue;
            }

            // Check batch quantities if specified
            if (line.BatchNumbers != null && line.BatchNumbers.Count > 0)
            {
                foreach (var batch in line.BatchNumbers)
                {
                    if (batch.Quantity <= 0)
                    {
                        result.Suggestions.Add(
                            $"Line {i + 1}: Batch '{batch.BatchNumber}' quantity must be greater than zero");
                        continue;
                    }

                    var hasSufficient = await HasSufficientBatchQuantityAsync(
                        itemCode, batch.BatchNumber ?? "", fromWarehouse, batch.Quantity, cancellationToken);

                    if (!hasSufficient)
                    {
                        result.Errors.Add(new StockValidationError
                        {
                            LineNumber = i + 1,
                            ItemCode = itemCode,
                            WarehouseCode = fromWarehouse,
                            RequestedQuantity = batch.Quantity,
                            AvailableQuantity = 0,
                            BatchNumber = batch.BatchNumber
                        });
                    }
                }
            }
            else
            {
                // Check overall item stock
                var availableQty = await GetAvailableQuantityAsync(itemCode, fromWarehouse, cancellationToken);
                if (line.Quantity > availableQty)
                {
                    result.Errors.Add(new StockValidationError
                    {
                        LineNumber = i + 1,
                        ItemCode = itemCode,
                        WarehouseCode = fromWarehouse,
                        RequestedQuantity = line.Quantity,
                        AvailableQuantity = availableQty
                    });
                }
            }
        }

        if (result.Errors.Count > 0)
        {
            result.Suggestions.Add("Verify source warehouse has sufficient stock before transfer");
            result.Suggestions.Add("For batch-managed items, ensure specified batches have sufficient quantities");
        }

        return result;
    }

    /// <inheritdoc/>
    public List<string> ValidatePositiveQuantities(IEnumerable<QuantityValidationItem> items)
    {
        var errors = new List<string>();

        foreach (var item in items)
        {
            if (item.Quantity <= 0)
            {
                errors.Add(
                    $"Line {item.LineNumber} (Item: {item.ItemCode ?? "unknown"}): " +
                    $"{item.FieldName ?? "Quantity"} must be greater than zero. " +
                    $"Current value: {item.Quantity}");
            }
        }

        return errors;
    }

    /// <inheritdoc/>
    public async Task<bool> HasSufficientBatchQuantityAsync(
        string itemCode,
        string batchNumber,
        string warehouseCode,
        decimal requestedQuantity,
        CancellationToken cancellationToken = default)
    {
        // Check local database first
        var localBatch = await _dbContext.ProductBatches
            .Include(b => b.Product)
            .FirstOrDefaultAsync(b =>
                b.Product.ItemCode == itemCode &&
                b.BatchNumber == batchNumber &&
                b.WarehouseCode == warehouseCode &&
                b.IsActive,
                cancellationToken);

        if (localBatch != null)
        {
            return localBatch.Quantity >= requestedQuantity;
        }

        // Fall back to SAP query
        try
        {
            var batches = await _sapClient.GetBatchNumbersForItemInWarehouseAsync(itemCode, warehouseCode, cancellationToken);
            var batch = batches?.FirstOrDefault(b =>
                string.Equals(b.BatchNum, batchNumber, StringComparison.OrdinalIgnoreCase));

            return batch != null && batch.Quantity >= requestedQuantity;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check batch quantity from SAP for {ItemCode}/{BatchNumber}",
                itemCode, batchNumber);
            return false; // Fail safe - assume insufficient
        }
    }

    /// <inheritdoc/>
    public async Task<decimal> GetAvailableQuantityAsync(
        string itemCode,
        string warehouseCode,
        CancellationToken cancellationToken = default)
    {
        // Check local database first
        var localProduct = await _dbContext.Products
            .FirstOrDefaultAsync(p => p.ItemCode == itemCode && p.IsActive, cancellationToken);

        if (localProduct != null && localProduct.LastSyncedAt.HasValue &&
            localProduct.LastSyncedAt.Value > DateTime.UtcNow.AddMinutes(-5))
        {
            // Use cached quantity if recently synced
            return localProduct.QuantityOnStock - localProduct.QuantityOrderedByCustomers;
        }

        // Fall back to SAP query
        try
        {
            var stockQuantities = await _sapClient.GetStockQuantitiesInWarehouseAsync(warehouseCode, cancellationToken);
            var stock = stockQuantities?.FirstOrDefault(s =>
                string.Equals(s.ItemCode, itemCode, StringComparison.OrdinalIgnoreCase));

            return stock?.Available ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get available quantity from SAP for {ItemCode} in {Warehouse}",
                itemCode, warehouseCode);
            return localProduct?.QuantityOnStock ?? 0;
        }
    }

    /// <inheritdoc/>
    public async Task UpdateLocalStockAsync(
        string itemCode,
        string warehouseCode,
        decimal quantityChange,
        string transactionType,
        CancellationToken cancellationToken = default)
    {
        var product = await _dbContext.Products
            .FirstOrDefaultAsync(p => p.ItemCode == itemCode, cancellationToken);

        if (product == null)
        {
            _logger.LogWarning("Product {ItemCode} not found in local database for stock update", itemCode);
            return;
        }

        var newQuantity = product.QuantityOnStock + quantityChange;

        // CRITICAL: Prevent negative stock
        if (newQuantity < 0)
        {
            _logger.LogError(
                "CRITICAL: Attempted to set negative stock for {ItemCode}. " +
                "Current: {Current}, Change: {Change}, Result would be: {Result}. " +
                "Transaction type: {TransactionType}",
                itemCode, product.QuantityOnStock, quantityChange, newQuantity, transactionType);

            throw new InvalidOperationException(
                $"Cannot complete {transactionType}: Would result in negative stock for item {itemCode}. " +
                $"Current stock: {product.QuantityOnStock}, Requested change: {quantityChange}");
        }

        product.QuantityOnStock = newQuantity;
        product.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Updated local stock for {ItemCode}: {OldQty} -> {NewQty} ({TransactionType})",
            itemCode, product.QuantityOnStock - quantityChange, newQuantity, transactionType);
    }
}
