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
    }
}
