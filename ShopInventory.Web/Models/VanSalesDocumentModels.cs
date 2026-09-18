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

    /// <summary>Invoices only: <c>Online</c> or <c>Offline</c>, or null for both.</summary>
    public string? Channel { get; set; }

    /// <summary>Credit notes only: <c>SAP</c> or <c>Till</c>, or null for both.</summary>
    public string? Origin { get; set; }

    /// <summary>Credit notes only. False leaves cancelled SAP memos out; they are still counted.</summary>
    public bool IncludeCancelled { get; set; } = true;
}

/// <summary>A sum of documents in one currency. Mirrors the API's <c>VanSalesMoneyTotal</c>.</summary>
public class VanSalesMoneyTotalModel
{
    public string Currency { get; set; } = "USD";
    public decimal Amount { get; set; }
    public decimal Vat { get; set; }
    public int Count { get; set; }

    /// <summary>How many of <see cref="Count"/> are in <see cref="Amount"/> without their VAT.</summary>
    public int NetOnlyCount { get; set; }
}

/// <summary>What a period's van invoices add up to, before the state filter.</summary>
public class VanSalesInvoiceSummaryModel
{
    public int Online { get; set; }
    public int Offline { get; set; }
    public List<VanSalesMoneyTotalModel> Totals { get; set; } = [];
    public int NotInSapCount { get; set; }
    public List<VanSalesMoneyTotalModel> NotInSap { get; set; } = [];
    public List<VanSalesVanBacklogModel> NotInSapByVan { get; set; } = [];
}

/// <summary>What one van has sold that SAP has not invoiced yet, in one currency.</summary>
public class VanSalesVanBacklogModel
{
    public string WarehouseCode { get; set; } = string.Empty;
    public string? RepName { get; set; }
    public string Currency { get; set; } = "USD";
    public decimal Amount { get; set; }
    public int Count { get; set; }
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
    public VanSalesInvoiceSummaryModel Summary { get; set; } = new();
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

    /// <summary><c>INV10427</c>, as Desktop Sales shows the same sale. Null for an old online sale with no sale row.</summary>
    public string? SaleNumber { get; set; }

    /// <summary>What the invoice is called on screen: its sale number, or its reference when it has none.</summary>
    public string Number => string.IsNullOrWhiteSpace(SaleNumber) ? Reference : SaleNumber;

    /// <summary>Whether the reference needs showing beside the number, because the number is not the reference.</summary>
    public bool HasSaleNumber => !string.IsNullOrWhiteSpace(SaleNumber);
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
    public List<VanSalesInvoiceCreditModel> CreditNotes { get; set; } = [];
}

/// <summary>A credit note against a van invoice, as the invoice's drawer states it.</summary>
public class VanSalesInvoiceCreditModel
{
    /// <summary>The key the credit notes list gives the same note.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary><c>SAP</c> or <c>Till</c>.</summary>
    public string Origin { get; set; } = string.Empty;

    public string Number { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string? Reason { get; set; }
    public bool IsCancelled { get; set; }

    /// <summary>Whether the amount has actually been given back, and so comes off the invoice.</summary>
    public bool GivesBack { get; set; }
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
    public VanSalesCreditNoteSummaryModel Summary { get; set; } = new();
}

/// <summary>What a period's van credit notes add up to.</summary>
public class VanSalesCreditNoteSummaryModel
{
    /// <summary>Before the origin filter.</summary>
    public int Sap { get; set; }

    /// <summary>Before the origin filter.</summary>
    public int Till { get; set; }

    /// <summary>Before cancelled notes are left out.</summary>
    public int Cancelled { get; set; }

    public int InvoicesReversed { get; set; }
    public List<VanSalesMoneyTotalModel> Credited { get; set; } = [];
    public List<VanSalesCustomerCreditModel> TopCustomers { get; set; } = [];
}

public class VanSalesCustomerCreditModel
{
    public string? CustomerCode { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string Currency { get; set; } = "USD";
    public decimal Amount { get; set; }
    public int Count { get; set; }
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

    /// <summary>The invoice total, when known. See <see cref="AmountIncludesVat"/> before setting it against a credit.</summary>
    public decimal? Amount { get; set; }
    public bool AmountIncludesVat { get; set; }
    public string? Currency { get; set; }
    public DateTime? SoldOn { get; set; }

    /// <summary><c>Online</c> or <c>Offline</c>.</summary>
    public string? Channel { get; set; }
    public string? WarehouseCode { get; set; }
}
