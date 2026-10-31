using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    /// <summary>Errors from the direct REVMax routes under <c>/api/revmax</c>.</summary>
    public static class Revmax
    {
        public static readonly Error Disabled = Error.Validation(
            "Revmax.Disabled",
            "REVMax is retired here (Revmax:Enabled is off), so it cannot be called.");

        public static Error DeviceUnavailable(string invoiceNumber, string detail) => Error.Failure(
            "Revmax.DeviceUnavailable",
            $"REVMax did not say whether it holds invoice '{invoiceNumber}': {detail}. Try again shortly.");

        public static Error InvoiceNotFound(string invoiceNumber) => Error.NotFound(
            "Revmax.InvoiceNotFound",
            $"REVMax holds no fiscal receipt for invoice '{invoiceNumber}'.");

        public static Error OwnDeviceReference(int deviceId) => Error.Validation(
            "Revmax.OwnDeviceReference",
            $"refDeviceId {deviceId} is REVMax's own device. TransactMExt is only for crediting a receipt another " +
            "device filed; sent here it would be filed as reversing some other device's receipt. Credit REVMax's " +
            "own receipts through the desktop credit-note flow instead.");

        public static Error AlreadyFiled(string invoiceNumber, long receiptGlobalNo, string? receiptType) => Error.Conflict(
            "Revmax.AlreadyFiled",
            $"REVMax already holds invoice '{invoiceNumber}' ({receiptType ?? "unknown type"}, receipt " +
            $"{receiptGlobalNo}). Nothing was sent. Use a new invoice number for a new credit note.");

        public static Error Refused(string invoiceNumber, string? code, string? message) => Error.Failure(
            "Revmax.Refused",
            $"REVMax refused TransactMExt for '{invoiceNumber}' (Code {code ?? "none"}): {message ?? "no message"}. " +
            "Nothing was filed.");

        public static Error OutcomeUnknown(string invoiceNumber, string detail) => Error.Failure(
            "Revmax.OutcomeUnknown",
            $"TransactMExt for '{invoiceNumber}' gave no clear answer ({detail}). It may or may not have been filed " +
            $"with ZIMRA. Check GET /api/revmax/invoices/{invoiceNumber} before sending it again.");
    }
}
