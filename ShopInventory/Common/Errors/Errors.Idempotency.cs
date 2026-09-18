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

        /// <summary>
        /// A post went to SAP and no answer came back, for a document SAP cannot be asked about
        /// afterwards. See <see cref="global::ShopInventory.Common.Idempotency.IdempotentCreate"/>.
        /// </summary>
        /// <remarks>
        /// Carries <see cref="OutcomeUnknownKey"/> so the claim guarding the post is kept rather than
        /// given back; a handler that has its own wording for the same outcome can add the key to
        /// its error instead (see <c>InventoryTransfer.SapPostUncertain</c>).
        /// </remarks>
        public static Error OutcomeUnknown(string operation) =>
            Error.Failure(
                "Idempotency.OutcomeUnknown",
                $"The {operation} request was sent to SAP but no answer came back, so SAP may already hold the "
                + "document. Check SAP before entering it again.",
                new Dictionary<string, object> { [OutcomeUnknownKey] = true });

        /// <summary>
        /// A retry under a key whose earlier attempt is still running, or reached SAP and never
        /// learned what became of it. Nothing has been sent.
        /// </summary>
        public static Error PostOutcomeUnconfirmed(string operation) =>
            Error.Conflict(
                "Idempotency.PostOutcomeUnconfirmed",
                $"An earlier {operation} request with these details is still running, or was sent to SAP without "
                + "an answer coming back. It has not been sent again. Check SAP for the document before entering it again.");

        /// <summary>Metadata key marking an error whose post may have reached SAP.</summary>
        public const string OutcomeUnknownKey = "outcomeUnknown";

        public static bool IsOutcomeUnknown(Error error) =>
            error.Metadata?.TryGetValue(OutcomeUnknownKey, out var value) == true && value is true;

        public static Error RequestMismatch(string operation) =>
            Error.Conflict(
                "Idempotency.RequestMismatch",
                $"The idempotency key for this {operation} request was already used with different payload");
    }
}