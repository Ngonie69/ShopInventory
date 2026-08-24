using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    /// <summary>
    /// Failures placing, reading or withdrawing a van sales customer's own order.
    /// </summary>
    /// <remarks>
    /// Note what is absent: there is no "that is not your order". An order belonging to another
    /// shop is reported as not found, exactly as one that never existed, because the two answers
    /// together would let a signed-in customer walk the id range and count a competitor's orders.
    /// </remarks>
    public static class VanSalesOrders
    {
        public static Error NotFound =>
            Error.NotFound(
                "VanSalesOrders.NotFound",
                "That order could not be found.");

        public static Error NoLines =>
            Error.Validation(
                "VanSalesOrders.NoLines",
                "Add at least one item before sending your order.");

        /// <summary>
        /// The order names items the customer cannot buy — withdrawn, deactivated, or never on the
        /// list.
        /// </summary>
        /// <remarks>
        /// Names the items rather than just refusing. A queued order can reach the server days
        /// after it was built, by which time an item may have been withdrawn; the app has to tell
        /// the shopkeeper which line to take out, and "your order was rejected" does not.
        /// </remarks>
        public static Error UnavailableItems(IEnumerable<string> itemCodes) =>
            Error.Validation(
                "VanSalesOrders.UnavailableItems",
                $"These items are no longer available: {string.Join(", ", itemCodes)}.");

        /// <summary>
        /// Ordering for that call has closed, or the date is not one this shop is called on.
        /// </summary>
        /// <remarks>
        /// One message for both, because the remedy is the same — order for the next call — and
        /// splitting them would mean explaining a delivery schedule in an error string.
        /// </remarks>
        public static Error OrderingClosed =>
            Error.Validation(
                "VanSalesOrders.OrderingClosed",
                "Ordering has closed for that delivery. Your order will need to go on the next one.");

        public static Error AlreadyCancelled =>
            Error.Conflict(
                "VanSalesOrders.AlreadyCancelled",
                "That order has already been cancelled.");

        /// <summary>
        /// The order has moved past the point where a customer may withdraw it.
        /// </summary>
        /// <remarks>
        /// Deliberately not silently ignored. Once the van is loaded — or the goods delivered — a
        /// cancellation is a conversation with the rep, not a button, and telling the customer it
        /// worked when it did not is how a shop refuses a delivery at the door.
        /// </remarks>
        public static Error CannotCancel =>
            Error.Conflict(
                "VanSalesOrders.CannotCancel",
                "That order can no longer be cancelled. Speak to your sales representative.");

        public static Error CancellationWindowClosed =>
            Error.Conflict(
                "VanSalesOrders.CancellationWindowClosed",
                "The cut-off for that delivery has passed, so the order can no longer be cancelled.");

        // ── Recording a delivery. Operator-facing, so these may name specifics. ──

        public static Error UnknownLines(IEnumerable<int> lineNumbers) =>
            Error.Validation(
                "VanSalesOrders.UnknownLines",
                $"That order has no line {string.Join(", ", lineNumbers)}.");

        /// <summary>
        /// More was recorded as delivered than was ordered.
        /// </summary>
        /// <remarks>
        /// Refused rather than accepted quietly. Extra goods handed over at the door are a sale the
        /// rep makes, and belong on an invoice — inflating the order the customer placed would put
        /// figures on their screen they never agreed to.
        /// </remarks>
        public static Error OverDelivered(IEnumerable<string> itemCodes) =>
            Error.Validation(
                "VanSalesOrders.OverDelivered",
                $"More was delivered than ordered for {string.Join(", ", itemCodes)}. Raise the extra on an invoice.");

        public static Error ChangedElsewhere =>
            Error.Conflict(
                "VanSalesOrders.ChangedElsewhere",
                "That order changed while you were working on it. Reload it and try again.");

        public static Error AlreadyConverted(string orderNumber) =>
            Error.Conflict(
                "VanSalesOrders.AlreadyConverted",
                $"Order {orderNumber} has already been converted to a sales order.");

        public static Error NotConvertible =>
            Error.Conflict(
                "VanSalesOrders.NotConvertible",
                "Only an accepted order can be converted to a sales order.");

        public static Error ConversionFailed(string message) =>
            Error.Failure("VanSalesOrders.ConversionFailed", message);
    }
}
