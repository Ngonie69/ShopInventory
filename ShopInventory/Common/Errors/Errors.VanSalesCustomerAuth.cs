using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    /// <summary>
    /// Failures on the van sales customer sign-in.
    /// </summary>
    /// <remarks>
    /// Note what is missing: there is no "no account for that number". Requesting a code succeeds
    /// for every well-formed number, registered or not, because an error that says otherwise turns
    /// the endpoint into a directory of who trades with us. The only failures a caller can observe
    /// are about the request itself or about a code they already hold.
    /// </remarks>
    public static class VanSalesCustomerAuth
    {
        public static Error InvalidPhoneNumber =>
            Error.Validation(
                "VanSalesCustomerAuth.InvalidPhoneNumber",
                "That does not look like a phone number. Check it and try again.");

        /// <summary>
        /// Wrong, expired, already used, or never issued — one message for all four.
        /// </summary>
        /// <remarks>
        /// Not split into "expired" versus "incorrect": telling an attacker that a guessed code was
        /// the right shape but too late confirms the code space is being searched in the right
        /// place. The customer's remedy is the same in every case — ask for another code.
        /// </remarks>
        public static Error InvalidCode =>
            Error.Validation(
                "VanSalesCustomerAuth.InvalidCode",
                "That code is not valid. Request a new one.");

        public static Error TooManyAttempts =>
            Error.Validation(
                "VanSalesCustomerAuth.TooManyAttempts",
                "Too many incorrect codes. Try again later.");

        public static Error SessionExpired =>
            Error.Unauthorized(
                "VanSalesCustomerAuth.SessionExpired",
                "Your session has ended. Sign in again.");

        public static Error AccountInactive =>
            Error.Unauthorized(
                "VanSalesCustomerAuth.AccountInactive",
                "This account is no longer active. Contact your sales representative.");

        // ── Operator-facing. These may be explicit: the caller is staff, already authenticated,
        // and entitled to know which customer they are looking at. The reticence above is owed to
        // anonymous callers, not to the person setting an account up.

        public static Error RouteCustomerNotFound(int routeCustomerId) =>
            Error.NotFound(
                "VanSalesCustomerAuth.RouteCustomerNotFound",
                $"No route customer with ID {routeCustomerId}.");

        public static Error RouteCustomerInactive(string code) =>
            Error.Validation(
                "VanSalesCustomerAuth.RouteCustomerInactive",
                $"Route customer {code} is not active and cannot be given an app sign-in.");

        public static Error PhoneAlreadyInUse(string maskedPhone) =>
            Error.Conflict(
                "VanSalesCustomerAuth.PhoneAlreadyInUse",
                $"{maskedPhone} already signs in for a different customer.");

        public static Error AccountNotFound(int accountId) =>
            Error.NotFound(
                "VanSalesCustomerAuth.AccountNotFound",
                $"No van sales customer sign-in with ID {accountId}.");
    }
}
