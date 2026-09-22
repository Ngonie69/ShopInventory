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
        public static Error Rejected(string message)
        {
            var defaults = UserDefaultsMismatch.Match(message);
            if (defaults.Success)
            {
                return DefaultsMismatch(defaults.Groups["setting"].Value);
            }

            return BareInternalError.IsMatch(message)
                ? Unexplained(message.Trim())
                : Error.Failure("SapUser.Rejected", message);
        }

        /// <summary>
        /// SAP refused and gave no reason at all: <c>Internal error (-5002) occurred</c> with empty
        /// details. Passing that through tells the operator nothing they can act on. The SAP client
        /// runs the same validation as the Service Layer, but it names the field it objects to, so
        /// the sentence sends them there.
        /// </summary>
        public static Error Unexplained(string sapMessage) =>
            Error.Failure(
                "SapUser.Unexplained",
                $"SAP refused this without saying why (it answered only \"{sapMessage}\"), so nothing was changed. " +
                "Make the change in the SAP client instead: Administration → Setup → General → Users - Setup, " +
                "find the user, untick Locked or set the password there, and click Update. If SAP refuses there too, it names what it objects to.");

        /// <summary>
        /// The one refusal whose own sentence is not enough. SAP re-validates the whole user on any
        /// write to <c>Users</c>, and refuses every change — unlock and password alike — while one of
        /// the user's settings differs from the User Defaults group they are assigned. The setting
        /// SAP names is not on the Service Layer's <c>User</c> entity, so this application cannot
        /// align it; somebody has to in the SAP client. The sentence says where.
        /// </summary>
        public static Error DefaultsMismatch(string setting) =>
            Error.Failure(
                "SapUser.DefaultsMismatch",
                $"SAP will not change this user while their \"{setting}\" setting differs from their User Defaults. " +
                "In the SAP client, open Administration → Setup → General → Users - Setup, find the user, " +
                "make that setting match their User Defaults and click Update, then try again.");

        private static readonly System.Text.RegularExpressions.Regex UserDefaultsMismatch = new(
            """^Checkbox "(?<setting>.+)" in "Users - Setup" is different to "User Defaults"\s*$""",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private static readonly System.Text.RegularExpressions.Regex BareInternalError = new(
            @"^\s*Internal error \(-?\d+\) occurred\.?\s*$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        public static readonly Error Unreachable =
            Error.Failure("SapUser.Unreachable", "SAP did not answer. The account was not changed — try again once SAP is back.");
    }
}
