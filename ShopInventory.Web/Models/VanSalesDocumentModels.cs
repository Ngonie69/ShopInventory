namespace ShopInventory.Web.Models;

/// <summary>
/// Where a van sales document stands at ZIMRA and in SAP. Mirrors <c>VanSalesDocumentStates</c> on the API,
/// which is the one that decides; these are only the words the page matches on.
/// </summary>
public static class VanSalesDocumentState
{
    public const string Complete = "Complete";
    public const string AwaitingSap = "AwaitingSap";
    public const string NotFiscalised = "NotFiscalised";
    public const string InProgress = "InProgress";
    public const string NeedsAttention = "NeedsAttention";
}

/// <summary>The filters both document lists take.</summary>
public class VanSalesDocumentFilter
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public Guid? RepUserId { get; set; }
    public string? State { get; set; }
    public string? Search { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public class VanSalesStateCounts
{
    public int All { get; set; }
    public int Complete { get; set; }
    public int AwaitingSap { get; set; }
    public int NotFiscalised { get; set; }
    public int InProgress { get; set; }
    public int NeedsAttention { get; set; }
}

public class VanSalesInvoicesResponse
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public VanSalesStateCounts Counts { get; set; } = new();
    public List<VanSalesInvoiceRowModel> Rows { get; set; } = [];
    public List<VanSalesRepOptionModel> Reps { get; set; } = [];
}

public class VanSalesInvoiceRowModel
{
    public string Reference { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public DateTime TradingDate { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public Guid? RepUserId { get; set; }
    public string? RepName { get; set; }
    public string? WarehouseCode { get; set; }
    public string? CustomerCode { get; set; }
    public string? CustomerName { get; set; }
    public string? PaymentMethod { get; set; }
    public decimal Amount { get; set; }
    public decimal? VatAmount { get; set; }
    public bool AmountIncludesVat { get; set; }
    public string Currency { get; set; } = "USD";
    public int? SapDocEntry { get; set; }
    public int? SapDocNum { get; set; }
    public string? FiscalReceiptNumber { get; set; }
    public string? FiscalVerificationCode { get; set; }
    public string? FiscalDay { get; set; }
    public string? FiscalDeviceSerial { get; set; }
    public string State { get; set; } = string.Empty;
    public string? Problem { get; set; }
}

public class VanSalesRepOptionModel
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class VanSalesInvoiceDetailModel
{
    public VanSalesInvoiceRowModel Invoice { get; set; } = new();
    public string? FiscalQrCode { get; set; }
    public int PostingAttempts { get; set; }
    public string? QueueStatus { get; set; }
    public List<VanSalesInvoiceLineModel> Lines { get; set; } = [];
}

public class VanSalesInvoiceLineModel
{
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public string? UoMCode { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal LineTotal { get; set; }
    public string? TaxCode { get; set; }
}

public class VanSalesCreditNotesResponse
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public bool SapProjectionCurrent { get; set; } = true;
    public VanSalesStateCounts Counts { get; set; } = new();
    public List<VanSalesCreditNoteRowModel> Rows { get; set; } = [];
}

public class VanSalesCreditNoteRowModel
{
    public string Key { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public string Number { get; set; } = string.Empty;
    public int? SapDocEntry { get; set; }
    public int? SapDocNum { get; set; }
    public string? CustomerCode { get; set; }
    public string? CustomerName { get; set; }
    public decimal Amount { get; set; }
    public decimal? VatAmount { get; set; }
    public string Currency { get; set; } = "USD";
    public string? Reason { get; set; }
    public bool IsCancelled { get; set; }
    public List<VanSalesCreditedInvoiceModel> CreditedInvoices { get; set; } = [];
    public string? FiscalReceiptNumber { get; set; }
    public string State { get; set; } = string.Empty;
    public string? Problem { get; set; }
}

public class VanSalesCreditedInvoiceModel
{
    public string Reference { get; set; } = string.Empty;
    public int? SapDocNum { get; set; }
    public string? CustomerName { get; set; }
    public string? RepName { get; set; }
}
