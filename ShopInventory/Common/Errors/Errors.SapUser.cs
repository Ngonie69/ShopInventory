using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    /// <summary>
    /// Failures of SAP user administration: reading the company's accounts, clearing a lock, and
    /// setting a password.
    /// </summary>
    public static class SapUser
    {
        public static readonly Error SapDisabled =
            Error.Failure("SapUser.SapDisabled", "SAP integration is disabled, so SAP user accounts cannot be read or changed.");

        public static Error NotFound(int internalKey) =>
            Error.NotFound("SapUser.NotFound", $"SAP has no user account {internalKey}.");

        /// <summary>
        /// Conflict rather than Validation on purpose. A list of all-Validation errors is answered as
        /// RFC 9457 validation problem details, whose <c>detail</c> is the generic "The request
        /// contains validation errors" and whose real sentences sit in the <c>errors</c> dictionary —
        /// and this sentence is the whole answer: the account is not locked, so whoever cannot sign in
        /// is failing on something else. A 409 puts it in <c>detail</c>, where every client reads it.
        /// </summary>
        public static Error NotLocked(string userCode) =>
            Error.Conflict("SapUser.NotLocked", $"SAP user '{userCode}' is not locked, so there is nothing to unlock.");

        /// <summary>
        /// SAP refused the write and said why. Its own sentence is passed through: it names the
        /// password rule that was broken, or the authorisation the Service Layer account is missing,
        /// and neither is something this application can restate more usefully.
        /// </summary>
        public static Error Rejected(string message) =>
            Error.Failure("SapUser.Rejected", message);

        public static readonly Error Unreachable =
            Error.Failure("SapUser.Unreachable", "SAP did not answer. The account was not changed — try again once SAP is back.");
    }
}
