using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShopInventory.Models.Revmax;

/// <summary>
/// Handles REVMax responses where Data can be an empty string instead of an object/null.
/// </summary>
public class EmptyStringToNullConverter<T> : JsonConverter<T?> where T : class
{
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            // REVMax returns "" for Data when not found — treat as null
            reader.GetString();
            return null;
        }

        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        return JsonSerializer.Deserialize<T>(ref reader, options);
    }

    public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, options);
    }
}

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
    public JsonElement? Data { get; set; }
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

    [JsonConverter(typeof(EmptyStringToNullConverter<InvoiceData>))]
    public InvoiceData? Data { get; set; }

    [JsonIgnore]
    public bool Success => Code == "1";
}

public class InvoiceData
{
    [JsonPropertyName("receiptType")]
    public string? ReceiptType { get; set; }

    [JsonPropertyName("receiptCurrency")]
    public string? ReceiptCurrency { get; set; }

    [JsonPropertyName("receiptCounter")]
    public int ReceiptCounter { get; set; }

    [JsonPropertyName("receiptGlobalNo")]
    public long ReceiptGlobalNo { get; set; }

    [JsonPropertyName("invoiceNo")]
    public string? InvoiceNo { get; set; }

    [JsonPropertyName("buyerData")]
    public string? BuyerData { get; set; }

    [JsonPropertyName("receiptNotes")]
    public string? ReceiptNotes { get; set; }

    [JsonPropertyName("receiptDate")]
    public string? ReceiptDate { get; set; }

    [JsonPropertyName("creditDebitNote")]
    public object? CreditDebitNote { get; set; }

    [JsonPropertyName("receiptLinesTaxInclusive")]
    public bool ReceiptLinesTaxInclusive { get; set; }

    [JsonPropertyName("receiptLines")]
    public List<ReceiptLine>? ReceiptLines { get; set; }

    [JsonPropertyName("receiptTaxes")]
    public List<ReceiptTax>? ReceiptTaxes { get; set; }

    [JsonPropertyName("receiptPayments")]
    public List<ReceiptPayment>? ReceiptPayments { get; set; }

    [JsonPropertyName("receiptTotal")]
    public decimal ReceiptTotal { get; set; }

    [JsonPropertyName("receiptPrintForm")]
    public string? ReceiptPrintForm { get; set; }

    // Legacy aliases used by controller code
    [JsonIgnore]
    public int FiscalDayNo => 0; // fiscal day is on the parent response, not Data

    // Keep backward compat for serialization to web client
    [JsonPropertyName("deviceSerial")]
    public string? DeviceSerial { get; set; }
}

public class ReceiptLine
{
    [JsonPropertyName("receiptLineName")]
    public string? ReceiptLineName { get; set; }

    [JsonPropertyName("receiptLineNo")]
    public int ReceiptLineNo { get; set; }

    [JsonPropertyName("receiptLineQuantity")]
    public decimal ReceiptLineQuantity { get; set; }

    [JsonPropertyName("receiptLineType")]
    public string? ReceiptLineType { get; set; }

    [JsonPropertyName("receiptLineTotal")]
    public decimal ReceiptLineTotal { get; set; }

    [JsonPropertyName("taxID")]
    public int TaxID { get; set; }

    [JsonPropertyName("receiptLineHSCode")]
    public string? ReceiptLineHSCode { get; set; }

    [JsonPropertyName("receiptLinePrice")]
    public decimal ReceiptLinePrice { get; set; }

    [JsonPropertyName("taxCode")]
    public string? TaxCode { get; set; }

    [JsonPropertyName("taxPercent")]
    public decimal TaxPercent { get; set; }
}

public class ReceiptTax
{
    [JsonPropertyName("salesAmountWithTax")]
    public decimal SalesAmountWithTax { get; set; }

    [JsonPropertyName("taxAmount")]
    public decimal TaxAmount { get; set; }

    [JsonPropertyName("taxID")]
    public int TaxID { get; set; }

    [JsonPropertyName("taxCode")]
    public string? TaxCode { get; set; }

    [JsonPropertyName("taxPercent")]
    public decimal TaxPercent { get; set; }
}

public class ReceiptPayment
{
    [JsonPropertyName("moneyTypeCode")]
    public string? MoneyTypeCode { get; set; }

    [JsonPropertyName("paymentAmount")]
    public decimal PaymentAmount { get; set; }
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
