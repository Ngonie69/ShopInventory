using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class Price
    {
        public static readonly Error SapDisabled =
            Error.Failure("Price.SapDisabled", "SAP integration is disabled");

        public static Error NotFound(string itemCode) =>
            Error.NotFound("Price.NotFound", $"No prices found for item '{itemCode}'");

        public static Error InvalidCurrency(string currency) =>
            Error.Validation("Price.InvalidCurrency", $"Invalid currency '{currency}'. Only USD and ZIG are supported.");

        public static Error SapError(string message) =>
            Error.Failure("Price.SapError", message);

        public static Error SapTimeout =
            Error.Failure("Price.SapTimeout", "Timeout connecting to SAP Service Layer");

        public static Error SapConnectionError(string message) =>
            Error.Failure("Price.SapConnectionError", $"Network error connecting to SAP: {message}");
    }
}
