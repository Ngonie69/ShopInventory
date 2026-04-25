using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class Stock
    {
        public static readonly Error SapDisabled =
            Error.Failure("Stock.SapDisabled", "SAP integration is disabled");

        public static Error SapError(string message) =>
            Error.Failure("Stock.SapError", message);

        public static readonly Error SapTimeout =
            Error.Failure("Stock.SapTimeout", "Timeout connecting to SAP Service Layer");

        public static Error SapConnectionError(string message) =>
            Error.Failure("Stock.SapConnectionError", $"Network error connecting to SAP: {message}");

        public static Error InvalidRequest(string message) =>
            Error.Validation("Stock.InvalidRequest", message);
    }
}
