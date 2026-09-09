using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class Idempotency
    {
        public static Error RequestInProgress(string operation) =>
            Error.Conflict(
                "Idempotency.RequestInProgress",
                $"An equivalent {operation} request is already in progress");

        /// <summary>
        /// An earlier attempt under this key issued its post and never came back to say what
        /// happened to it, and the system of record does not show the document — yet.
        /// </summary>
        /// <remarks>
        /// Deliberately distinct from <see cref="RequestInProgress"/>, which says another request is
        /// running right now. This one says nothing is running and we still cannot answer: the
        /// document may exist and simply not be visible. The caller is being asked to wait rather
        /// than being told its request failed, and the reference is named so a person can look for
        /// themselves instead of guessing.
        /// </remarks>
        public static Error PostOutcomeUnknown(string operation, string reference) =>
            Error.Conflict(
                "Idempotency.PostOutcomeUnknown",
                $"An earlier {operation} request was sent to SAP and its outcome is not yet known, so this "
                + "one has not been sent again — SAP may already hold the document. Retry shortly, or check SAP "
                + $"for '{reference}'.");

        public static Error RequestMismatch(string operation) =>
            Error.Conflict(
                "Idempotency.RequestMismatch",
                $"The idempotency key for this {operation} request was already used with different payload");
    }
}