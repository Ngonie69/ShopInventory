namespace ShopInventory.Web.Features.Reports.Queries.GetFiscalTransactionLog;

public sealed class FiscalTransactionLogItemModel
{
    public int Id { get; init; }
    public string ClientTransactionId { get; init; } = string.Empty;
    public DateTime TimestampUtc { get; init; }
    public string DocumentType { get; init; } = string.Empty;
    public int DocNum { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? Message { get; init; }
    public string? VerificationCode { get; init; }
    public string? QRCode { get; init; }
    public string? DeviceSerialNumber { get; init; }
    public string? DeviceId { get; init; }
    public string? FiscalDay { get; init; }
    public int? ReceiptGlobalNo { get; init; }
    public string? CardCode { get; init; }
    public string? CardName { get; init; }
    public decimal DocTotal { get; init; }
    public decimal VatSum { get; init; }
    public string? Currency { get; init; }
    public string? OriginalInvoiceNumber { get; init; }
    public string SourceSystem { get; init; } = string.Empty;
    public string? CreatedByUserId { get; init; }
    public string? CreatedByUsername { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime LastSyncedAtUtc { get; init; }
}