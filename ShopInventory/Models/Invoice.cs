using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ShopInventory.Models;

public class Invoice
{
    [JsonPropertyName("DocEntry")]
    public int DocEntry { get; set; }

    [JsonPropertyName("DocNum")]
    public int DocNum { get; set; }

    [JsonPropertyName("DocDate")]
    public string? DocDate { get; set; }

    [JsonPropertyName("DocDueDate")]
    public string? DocDueDate { get; set; }

    [JsonPropertyName("CardCode")]
    [Required(ErrorMessage = "Customer code is required")]
    public string? CardCode { get; set; }

    [JsonPropertyName("CardName")]
    public string? CardName { get; set; }

    [JsonPropertyName("NumAtCard")]
    public string? NumAtCard { get; set; } // Customer reference number

    [JsonPropertyName("Comments")]
    public string? Comments { get; set; }

    [JsonPropertyName("DocTotal")]
    public decimal DocTotal { get; set; }

    [JsonPropertyName("DocTotalFc")]
    public decimal DocTotalFc { get; set; }

    [JsonPropertyName("VatSum")]
    public decimal VatSum { get; set; }

    [JsonPropertyName("DocCurrency")]
    public string? DocCurrency { get; set; }

    [JsonPropertyName("SalesPersonCode")]
    public int? SalesPersonCode { get; set; }

    [JsonPropertyName("DocumentLines")]
    [Required(ErrorMessage = "At least one document line is required")]
    [MinLength(1, ErrorMessage = "At least one document line is required")]
    public List<InvoiceLine>? DocumentLines { get; set; }
}

public class InvoiceLine
{
    [JsonPropertyName("LineNum")]
    public int LineNum { get; set; }

    [JsonPropertyName("ItemCode")]
    [Required(ErrorMessage = "Item code is required")]
    public string? ItemCode { get; set; }

    [JsonPropertyName("ItemDescription")]
    public string? ItemDescription { get; set; }

    [JsonPropertyName("Quantity")]
    [Range(0.00001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("UnitPrice")]
    [Range(0, double.MaxValue, ErrorMessage = "Unit price cannot be negative")]
    public decimal UnitPrice { get; set; }

    [JsonPropertyName("Price")]
    public decimal Price { get; set; }

    [JsonPropertyName("LineTotal")]
    public decimal LineTotal { get; set; }

    [JsonPropertyName("WarehouseCode")]
    public string? WarehouseCode { get; set; }

    [JsonPropertyName("TaxCode")]
    public string? TaxCode { get; set; }

    [JsonPropertyName("DiscountPercent")]
    [Range(0, 100, ErrorMessage = "Discount percent must be between 0 and 100")]
    public decimal DiscountPercent { get; set; }

    [JsonPropertyName("UoMCode")]
    public string? UoMCode { get; set; }

    [JsonPropertyName("UoMEntry")]
    public int? UoMEntry { get; set; }

    /// <summary>
    /// Batch numbers used in this invoice line (returned by SAP)
    /// </summary>
    [JsonPropertyName("BatchNumbers")]
    public List<InvoiceLineBatch>? BatchNumbers { get; set; }
}

/// <summary>
/// Batch number information from SAP invoice line
/// </summary>
public class InvoiceLineBatch
{
    [JsonPropertyName("BatchNumber")]
    public string? BatchNumber { get; set; }

    [JsonPropertyName("Quantity")]
    public decimal Quantity { get; set; }
}

public class CreateInvoiceRequest
{
    [Required(ErrorMessage = "Customer code is required")]
    public string? CardCode { get; set; }

    public string? DocDate { get; set; }

    public string? DocDueDate { get; set; }

    /// <summary>
    /// Customer reference number (required)
    /// </summary>
    [Required(ErrorMessage = "NumAtCard (customer reference) is required")]
    public string? NumAtCard { get; set; }

    public string? Comments { get; set; }

    public string? DocCurrency { get; set; }

    public int? SalesPersonCode { get; set; }

    [Required(ErrorMessage = "At least one line item is required")]
    [MinLength(1, ErrorMessage = "At least one line item is required")]
    public List<CreateInvoiceLineRequest>? Lines { get; set; }
}

public class CreateInvoiceLineRequest
{
    [Required(ErrorMessage = "Item code is required")]
    public string? ItemCode { get; set; }

    [Required(ErrorMessage = "Quantity is required")]
    [Range(0.00001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero")]
    public decimal Quantity { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "Unit price cannot be negative")]
    public decimal? UnitPrice { get; set; }

    /// <summary>
    /// Warehouse code (REQUIRED for batch validation - prevents negative quantities)
    /// </summary>
    [Required(ErrorMessage = "Warehouse code is required for each line")]
    public string? WarehouseCode { get; set; }

    public string? TaxCode { get; set; }

    [Range(0, 100, ErrorMessage = "Discount percent must be between 0 and 100")]
    public decimal? DiscountPercent { get; set; }

    /// <summary>
    /// Unit of Measure code (e.g., "KG", "PC", "BOX")
    /// Used for quantity conversion to inventory UoM
    /// </summary>
    public string? UoMCode { get; set; }

    /// <summary>
    /// SAP UoM Entry identifier (alternative to UoMCode)
    /// </summary>
    public int? UoMEntry { get; set; }

    /// <summary>
    /// G/L Account code for the line
    /// </summary>
    public string? AccountCode { get; set; }

    /// <summary>
    /// Batch numbers for batch-managed items.
    /// REQUIRED for batch-managed items unless auto-allocation is enabled.
    /// Sum of batch quantities must equal line quantity (in inventory UoM).
    /// </summary>
    public List<BatchNumberRequest>? BatchNumbers { get; set; }

    /// <summary>
    /// Whether to auto-allocate batches using FIFO/FEFO if BatchNumbers is not specified.
    /// Defaults to true. Set to false to require explicit batch allocation.
    /// </summary>
    public bool AutoAllocateBatches { get; set; } = true;
}

/// <summary>
/// Batch number details for invoice line
/// </summary>
public class BatchNumberRequest
{
    /// <summary>
    /// The batch number (must exist in the specified warehouse)
    /// </summary>
    [Required(ErrorMessage = "Batch number is required")]
    public string? BatchNumber { get; set; }

    /// <summary>
    /// Quantity from this batch (in inventory UoM, must be positive)
    /// </summary>
    [Required(ErrorMessage = "Quantity is required")]
    [Range(0.00001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero")]
    public decimal Quantity { get; set; }

    /// <summary>
    /// Expiry date of the batch (for information/validation)
    /// </summary>
    public DateTime? ExpiryDate { get; set; }
}
