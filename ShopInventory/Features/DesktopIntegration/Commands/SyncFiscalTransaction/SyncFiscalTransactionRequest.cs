namespace ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;

public sealed class SyncFiscalTransactionRequest
{
    public string ClientTransactionId { get; set; } = string.Empty;
    public DateTime? TimestampUtc { get; set; }
    public int DocNum { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Message { get; set; }
    public string? VerificationCode { get; set; }
    public string? QRCode { get; set; }
    public string? DeviceSerialNumber { get; set; }
    public string? DeviceId { get; set; }
    public string? FiscalDay { get; set; }
    public int? ReceiptGlobalNo { get; set; }
    public string? CardCode { get; set; }
    public string? CardName { get; set; }
    public decimal DocTotal { get; set; }
    public decimal VatSum { get; set; }
    public string? Currency { get; set; }
    public string? OriginalInvoiceNumber { get; set; }
    public string? RawRequest { get; set; }
    public string? RawResponse { get; set; }
    public string? SourceSystem { get; set; }
}