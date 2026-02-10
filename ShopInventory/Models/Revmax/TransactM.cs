using System.Text.Json.Serialization;

namespace ShopInventory.Models.Revmax;

/// <summary>
/// Request DTO for TransactM endpoint.
/// </summary>
public class TransactMRequest
{
    /// <summary>
    /// Currency code (e.g., ZWG).
    /// </summary>
    public string? Currency { get; set; }

    /// <summary>
    /// Branch name for the transaction.
    /// </summary>
    public string? BranchName { get; set; }

    /// <summary>
    /// Invoice number (required).
    /// </summary>
    public string? InvoiceNumber { get; set; }

    /// <summary>
    /// Original invoice number for credit notes.
    /// If populated, this transaction is a credit note.
    /// </summary>
    public string? OriginalInvoiceNumber { get; set; }

    /// <summary>
    /// Customer name.
    /// </summary>
    public string? CustomerName { get; set; }

    /// <summary>
    /// Customer VAT number.
    /// </summary>
    public string? CustomerVatNumber { get; set; }

    /// <summary>
    /// Customer address.
    /// </summary>
    public string? CustomerAddress { get; set; }

    /// <summary>
    /// Customer telephone.
    /// </summary>
    public string? CustomerTelephone { get; set; }

    /// <summary>
    /// Customer email.
    /// </summary>
    public string? CustomerEmail { get; set; }

    /// <summary>
    /// Customer BPN (Business Partner Number).
    /// </summary>
    public string? CustomerBPN { get; set; }

    /// <summary>
    /// Total invoice amount (VAT inclusive).
    /// </summary>
    public decimal InvoiceAmount { get; set; }

    /// <summary>
    /// Total tax amount.
    /// </summary>
    public decimal InvoiceTaxAmount { get; set; }

    /// <summary>
    /// Invoice status. "02" for credit notes.
    /// </summary>
    public string? Istatus { get; set; }

    /// <summary>
    /// Cashier/user posting the transaction.
    /// </summary>
    public string? Cashier { get; set; }

    /// <summary>
    /// Optional comment/reason.
    /// </summary>
    public string? InvoiceComment { get; set; }

    /// <summary>
    /// XML string containing line items.
    /// </summary>
    public string? ItemsXml { get; set; }

    /// <summary>
    /// XML string containing currencies received.
    /// </summary>
    public string? CurrenciesXml { get; set; }
}

/// <summary>
/// Response DTO from TransactM endpoint.
/// </summary>
public class TransactMResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? QRcode { get; set; }
    public string? FiscalDayNo { get; set; }
    public string? ReceiptGlobalNo { get; set; }
    public string? ReceiptCounter { get; set; }
    public string? DeviceSerial { get; set; }
    public string? FiscalDayDate { get; set; }
    public string? ReceiptDate { get; set; }
    public string? VerificationCode { get; set; }
    public object? Data { get; set; }
}
