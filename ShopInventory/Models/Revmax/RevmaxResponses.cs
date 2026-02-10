using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShopInventory.Models.Revmax;

/// <summary>
/// Card details response from REVMax.
/// </summary>
public class CardDetailsResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public CardDetailsData? Data { get; set; }
}

public class CardDetailsData
{
    public string? DeviceSerial { get; set; }
    public string? TaxpayerName { get; set; }
    public string? TaxpayerTIN { get; set; }
    public string? TaxpayerBPN { get; set; }
    public string? TaxpayerVATNo { get; set; }
    public string? Address { get; set; }
}

/// <summary>
/// Day status response from REVMax.
/// </summary>
public class DayStatusResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public DayStatusData? Data { get; set; }
}

public class DayStatusData
{
    public bool DayOpened { get; set; }
    public int FiscalDayNo { get; set; }
    public string? FiscalDayDate { get; set; }
    public int ReceiptCounter { get; set; }
}

/// <summary>
/// License response from REVMax.
/// </summary>
public class LicenseResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public LicenseData? Data { get; set; }
}

public class LicenseData
{
    public string? License { get; set; }
    public string? ExpiryDate { get; set; }
    public bool IsValid { get; set; }
}

/// <summary>
/// Z-Report response from REVMax.
/// </summary>
public class ZReportResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public object? Data { get; set; }
}

/// <summary>
/// Invoice lookup response from REVMax.
/// </summary>
public class InvoiceResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public InvoiceData? Data { get; set; }
    public string? QRcode { get; set; }
}

public class InvoiceData
{
    public string? InvoiceNumber { get; set; }
    public string? Currency { get; set; }
    public string? BranchName { get; set; }
    public decimal InvoiceAmount { get; set; }
    public decimal InvoiceTaxAmount { get; set; }
    public string? Istatus { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerVatNumber { get; set; }
    public string? Cashier { get; set; }

    [JsonPropertyName("receiptGlobalNo")]
    public long ReceiptGlobalNo { get; set; }

    [JsonPropertyName("fiscalDayNo")]
    public int FiscalDayNo { get; set; }

    [JsonPropertyName("deviceSerial")]
    public string? DeviceSerial { get; set; }

    [JsonPropertyName("receiptDate")]
    public string? ReceiptDate { get; set; }
}

/// <summary>
/// Unprocessed invoices summary response from REVMax.
/// </summary>
public class UnprocessedInvoicesSummaryResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public List<UnprocessedInvoiceSummary>? Data { get; set; }
}

public class UnprocessedInvoiceSummary
{
    public string? InvoiceNumber { get; set; }
    public decimal InvoiceAmount { get; set; }
    public string? Currency { get; set; }
    public string? Status { get; set; }
    public string? CreatedDate { get; set; }
}

/// <summary>
/// Generic error response wrapper.
/// </summary>
public class RevmaxErrorResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? ErrorCode { get; set; }
    public object? Details { get; set; }
}
