namespace ShopInventory.Features.Invoices;

/// <summary>
/// A fiscal receipt the caller has already verified, to print on the invoice in place of whatever the
/// DocNum-keyed lookups would find.
/// </summary>
/// <remarks>
/// The verification code, fiscal day and device are printed beside the QR, so they travel with it: a
/// verified QR printed next to another document's verification code is a wrong invoice either way.
/// </remarks>
public sealed record InvoicePdfReceipt(
    string? QrCode,
    string? VerificationCode,
    string? FiscalDay,
    string? DeviceId,
    int? ReceiptGlobalNo);
