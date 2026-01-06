namespace ShopInventory.Web.Models;

public class InvoiceDto
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    public string? DocDate { get; set; }
    public string? DocDueDate { get; set; }
    public string? CardCode { get; set; }
    public string? CardName { get; set; }
    public string? NumAtCard { get; set; }
    public string? Comments { get; set; }
    public decimal DocTotal { get; set; }
    public decimal VatSum { get; set; }
    public string? DocCurrency { get; set; }
    public List<InvoiceLineDto>? Lines { get; set; }
}

public class InvoiceLineDto
{
    public int LineNum { get; set; }
    public string? ItemCode { get; set; }
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    public string? WarehouseCode { get; set; }
    public decimal DiscountPercent { get; set; }
    public string? UoMCode { get; set; }
}

public class InvoiceListResponse
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Count { get; set; }
    public bool HasMore { get; set; }
    public List<InvoiceDto>? Invoices { get; set; }
}

public class InvoiceDateResponse
{
    public string? Date { get; set; }
    public string? FromDate { get; set; }
    public string? ToDate { get; set; }
    public string? Customer { get; set; }
    public int Count { get; set; }
    public List<InvoiceDto>? Invoices { get; set; }
}

public class CreateInvoiceRequest
{
    public string CardCode { get; set; } = string.Empty;
    public string? NumAtCard { get; set; }
    public string? Comments { get; set; }
    public string? DocCurrency { get; set; }
    public List<CreateInvoiceLineRequest> Lines { get; set; } = new();
}

public class CreateInvoiceLineRequest
{
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public decimal? UnitPrice { get; set; }

    /// <summary>
    /// Warehouse code (REQUIRED for batch validation - prevents negative quantities)
    /// </summary>
    public string WarehouseCode { get; set; } = string.Empty;

    public decimal? DiscountPercent { get; set; }

    /// <summary>
    /// Unit of Measure code (e.g., "KG", "PC", "BOX")
    /// </summary>
    public string? UoMCode { get; set; }

    /// <summary>
    /// SAP UoM Entry identifier
    /// </summary>
    public int? UoMEntry { get; set; }

    /// <summary>
    /// Single batch number (for simple allocation) - REQUIRED for batch-managed items
    /// </summary>
    public string? BatchNumber { get; set; }

    /// <summary>
    /// Multiple batch allocations for batch-managed items.
    /// Sum of batch quantities must equal line quantity (in inventory UoM).
    /// </summary>
    public List<BatchNumberRequest>? BatchNumbers { get; set; }

    /// <summary>
    /// Whether to auto-allocate batches using FIFO/FEFO if BatchNumbers is not specified.
    /// </summary>
    public bool AutoAllocateBatches { get; set; } = true;

    /// <summary>
    /// G/L Account code for revenue posting
    /// </summary>
    public string? AccountCode { get; set; }
}

/// <summary>
/// Batch number details for invoice line
/// </summary>
public class BatchNumberRequest
{
    /// <summary>
    /// The batch number
    /// </summary>
    public string BatchNumber { get; set; } = string.Empty;

    /// <summary>
    /// Quantity from this batch (in inventory UoM)
    /// </summary>
    public decimal Quantity { get; set; }

    /// <summary>
    /// Expiry date of the batch
    /// </summary>
    public DateTime? ExpiryDate { get; set; }
}

public class InvoiceCreatedResponse
{
    public string Message { get; set; } = string.Empty;
    public InvoiceDto? Invoice { get; set; }
}
