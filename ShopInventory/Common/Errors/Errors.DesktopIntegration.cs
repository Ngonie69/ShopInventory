using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class DesktopIntegration
    {
        public static Error ReservationNotFound(string id) =>
            Error.NotFound("DesktopIntegration.ReservationNotFound", $"Reservation '{id}' not found");

        public static Error ReservationFailed(string message) =>
            Error.Failure("DesktopIntegration.ReservationFailed", message);

        public static Error ConfirmationFailed(string message) =>
            Error.Failure("DesktopIntegration.ConfirmationFailed", message);

        public static Error CancellationFailed(string message) =>
            Error.Failure("DesktopIntegration.CancellationFailed", message);

        public static Error InvoiceCreationFailed(string message) =>
            Error.Failure("DesktopIntegration.InvoiceCreationFailed", message);

        public static Error QueueNotFound(string reference) =>
            Error.NotFound("DesktopIntegration.QueueNotFound", $"Queue entry '{reference}' not found");

        public static Error TransferFailed(string message) =>
            Error.Failure("DesktopIntegration.TransferFailed", message);

        public static Error TransferRequestFailed(string message) =>
            Error.Failure("DesktopIntegration.TransferRequestFailed", message);

        /// <summary>
        /// The post was already in flight when it failed, so SAP may hold the document.
        /// </summary>
        /// <remarks>
        /// Must not read as "try again": the retry is how a lost reply becomes two transfer
        /// requests for the same movement. Mirrors
        /// <see cref="Errors.InventoryTransfer.SapPostUncertain"/>.
        /// </remarks>
        public static Error TransferRequestPostUncertain =>
            Error.Failure(
                "DesktopIntegration.TransferRequestPostUncertain",
                "The transfer request was sent to SAP but the reply was lost. Check SAP for the document before creating it again.");

        /// <summary>
        /// A stock request from an account with no shop that named no source warehouse. The same
        /// sentence the request's model validation used to answer with, now the handler's to give,
        /// because an account on a shop no longer has to send one.
        /// </summary>
        public static Error SourceWarehouseRequired =>
            Error.Validation("DesktopIntegration.SourceWarehouseRequired", "Source warehouse is required");

        public static Error ValidationFailed(string message) =>
            Error.Failure("DesktopIntegration.ValidationFailed", message);

        // A sales order converted on a van is signed before it is answered. These are the device's answers
        // that are not a refusal of the order, and the handset reads each one by its wording
        // (KefalosVanSales ConversionOutcome.NotSigned) — keep the phrases if the sentences change.

        /// <summary>
        /// The device could not say whether it signed. The receipt may exist, so this must never read as
        /// "try again under a new reference": the conversion is queued for a person, and the order with it.
        /// </summary>
        public static Error ConversionFiscalOutcomeUnknown =>
            Error.Conflict(
                "DesktopIntegration.ConversionFiscalOutcomeUnknown",
                "The fiscal device did not confirm whether this invoice's receipt was issued. Do not convert " +
                "this order again — the office will check the device and confirm the invoice.");

        /// <summary>The device could not be asked. Nothing was signed; the stock stays held for a resend.</summary>
        public static Error ConversionFiscalDeviceUnavailable =>
            Error.Failure(
                "DesktopIntegration.ConversionFiscalDeviceUnavailable",
                "The fiscal device could not be reached, so this invoice was not raised and nothing was " +
                "charged. Convert the order again in a moment.");

        /// <summary>The device refused. Nothing was signed; the stock stays held for a resend.</summary>
        public static Error ConversionFiscalisationFailed(string? reason) =>
            Error.Failure(
                "DesktopIntegration.ConversionFiscalisationFailed",
                "The invoice could not be fiscalised, so it was not raised and nothing was charged: " +
                (string.IsNullOrWhiteSpace(reason) ? "the fiscal device refused the receipt." : reason));

        public static Error FiscalTransactionSyncFailed(string message) =>
            Error.Failure("DesktopIntegration.FiscalTransactionSyncFailed", message);

        public static Error SapError(string message) =>
            Error.Failure("DesktopIntegration.SapError", message);

        public static readonly Error SapDisabled =
            Error.Failure("DesktopIntegration.SapDisabled", "SAP integration is disabled");

        public static Error InvoiceNotFound(int docEntry) =>
            Error.NotFound("DesktopIntegration.InvoiceNotFound", $"Invoice with DocEntry {docEntry} not found");

        public static Error TransferNotFound(int docEntry) =>
            Error.NotFound("DesktopIntegration.TransferNotFound", $"Transfer with DocEntry {docEntry} not found");

        public static Error TransferRequestNotFound(int docEntry) =>
            Error.NotFound("DesktopIntegration.TransferRequestNotFound", $"Transfer request with DocEntry {docEntry} not found");

        /// <summary>
        /// This API is configured not to call TransferEventListener.
        /// </summary>
        /// <remarks>
        /// Says what is still working on purpose. The switch only governs calls out to the listener;
        /// the listener's inbound webhook is untouched and keeps applying transfers to the snapshot,
        /// so this must not read as "transfers have stopped".
        /// </remarks>
        public static Error TransferListenerDisabled() =>
            Error.Failure(
                "DesktopIntegration.TransferListenerDisabled",
                "This API is configured not to call TransferEventListener "
                + "(TransferEventListener:Enabled is false). Its inbound webhook is unaffected.");

        public static Error TransferListenerUnreachable(string baseUrl, string message) =>
            Error.Failure(
                "DesktopIntegration.TransferListenerUnreachable",
                $"TransferEventListener at {baseUrl} could not be reached: {message}");
    }
}
