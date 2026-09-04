using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class RateLimit
    {
        public static Error ClientNotFound(string clientId) =>
            Error.NotFound("RateLimit.ClientNotFound", $"Rate limit record for client '{clientId}' not found");

        public static Error BlockFailed(string message) =>
            Error.Failure("RateLimit.BlockFailed", message);

        public static Error UpdateFailed(string message) =>
            Error.Failure("RateLimit.UpdateFailed", message);

        /// <summary>
        /// A configuration change that the limiter could not run. Validation, not taste: ASP.NET
        /// Core throws when a window limiter is built with a permit limit below 1 or a zero
        /// window, and it throws inside the partition factory - on the request path, for every
        /// request. Saving one would take the API down until somebody edited the database.
        /// </summary>
        public static Error InvalidConfiguration(string message) =>
            Error.Validation("RateLimit.InvalidConfiguration", message);
    }
}
