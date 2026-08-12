namespace ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;

/// <summary>
/// The row a desktop client gets back when it syncs a fiscal transaction. It
/// outlived the read endpoints that used to share it, so it lives with its
/// only remaining caller.
/// </summary>
public sealed class FiscalTransactionLogItemDto
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
    public string? RawRequest { get; init; }
    public string? RawResponse { get; init; }
    public string SourceSystem { get; init; } = string.Empty;
    public string? CreatedByUserId { get; init; }
    public string? CreatedByUsername { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime LastSyncedAtUtc { get; init; }
}