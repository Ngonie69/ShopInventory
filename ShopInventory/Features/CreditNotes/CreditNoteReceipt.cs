namespace ShopInventory.Features.CreditNotes;

/// <summary>The fiscal receipt ZIMRA holds for a credit memo, as a customer may be shown it.</summary>
/// <param name="ReceiptGlobalNo">The receipt's number on its device.</param>
/// <param name="QrCode">The QR payload: the address ZIMRA verifies the receipt at.</param>
/// <param name="VerificationCode">The code printed beside the QR, raw or grouped as the device sent it.</param>
/// <param name="FiscalDay">The device's fiscal day the receipt was signed on.</param>
/// <param name="DeviceId">The device that signed it.</param>
public sealed record CreditNoteReceipt(
    int? ReceiptGlobalNo,
    string? QrCode,
    string? VerificationCode,
    string? FiscalDay,
    string? DeviceId);
