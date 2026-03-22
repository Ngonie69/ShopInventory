using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShopInventory.Models.Revmax;

/// <summary>
/// Card details response from REVMax.
/// </summary>
public class CardDetailsResponse
{
    public string? Code { get; set; }
    public string? Message { get; set; }
    public string? QRcode { get; set; }
    public string? VerificationCode { get; set; }
    public string? VerificationLink { get; set; }
    public string? DeviceID { get; set; }
    public string? DeviceSerialNumber { get; set; }
    public string? FiscalDay { get; set; }
    public CardDetailsData? Data { get; set; }
}

public class CardDetailsData
{
    public string? TIN { get; set; }
    public string? BPN { get; set; }
    public string? VAT { get; set; }
    public string? COMPANYNAME { get; set; }
    public string? ADDRESS { get; set; }
    public string? REGISTRATIONNUMBER { get; set; }
    public string? SERIALNUMBER { get; set; }
}

/// <summary>
/// Day status response from REVMax.
/// </summary>
public class DayStatusResponse
{
    public string? Code { get; set; }
    public string? Message { get; set; }
    public string? QRcode { get; set; }
    public string? VerificationCode { get; set; }
    public string? VerificationLink { get; set; }
    public string? DeviceID { get; set; }
    public string? DeviceSerialNumber { get; set; }
    public string? FiscalDay { get; set; }
    public DayStatusData? Data { get; set; }
}

public class DayStatusData
{
    [JsonPropertyName("fiscalDayStatus")]
    public string? FiscalDayStatus { get; set; }

    [JsonPropertyName("lastReceiptGlobalNo")]
    public int LastReceiptGlobalNo { get; set; }

    [JsonPropertyName("lastFiscalDayNo")]
    public int LastFiscalDayNo { get; set; }

    [JsonPropertyName("operationID")]
    public string? OperationID { get; set; }
}

/// <summary>
/// License response from REVMax.
/// </summary>
public class LicenseResponse
{
    public string? Code { get; set; }
    public string? Message { get; set; }
    public string? QRcode { get; set; }
    public string? VerificationCode { get; set; }
    public string? VerificationLink { get; set; }
    public string? DeviceID { get; set; }
    public string? DeviceSerialNumber { get; set; }
    public string? FiscalDay { get; set; }
    public LicenseData? Data { get; set; }
}

public class LicenseData
{
    public string? Status { get; set; }
    public string? Start { get; set; }
    public string? End { get; set; }
}

/// <summary>
/// Z-Report response from REVMax.
/// </summary>
public class ZReportResponse
{
    public string? Code { get; set; }
    public string? Message { get; set; }
    public string? QRcode { get; set; }
    public string? VerificationCode { get; set; }
    public string? VerificationLink { get; set; }
    public string? DeviceID { get; set; }
    public string? DeviceSerialNumber { get; set; }
    public string? FiscalDay { get; set; }
    public object? Data { get; set; }
}

/// <summary>
/// Invoice lookup response from REVMax.
/// </summary>
public class InvoiceResponse
{
    public string? Code { get; set; }
    public string? Message { get; set; }
    public string? QRcode { get; set; }
    public string? VerificationCode { get; set; }
    public string? VerificationLink { get; set; }
    public string? DeviceID { get; set; }
    public string? DeviceSerialNumber { get; set; }
    public string? FiscalDay { get; set; }
    public InvoiceData? Data { get; set; }

    [JsonIgnore]
    public bool Success => Code == "1";
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
    public string? Code { get; set; }
    public string? Message { get; set; }
    public string? QRcode { get; set; }
    public string? VerificationCode { get; set; }
    public string? VerificationLink { get; set; }
    public string? DeviceID { get; set; }
    public string? DeviceSerialNumber { get; set; }
    public string? FiscalDay { get; set; }
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
    public string? Code { get; set; }
    public string? Message { get; set; }
    public string? ErrorCode { get; set; }
    public object? Details { get; set; }
}
