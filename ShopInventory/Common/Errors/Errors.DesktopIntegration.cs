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

        public static Error ValidationFailed(string message) =>
            Error.Failure("DesktopIntegration.ValidationFailed", message);

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
    }
}
