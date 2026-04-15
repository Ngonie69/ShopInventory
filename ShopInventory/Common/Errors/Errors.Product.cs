using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class Product
    {
        public static Error NotFound(string itemCode) =>
            Error.NotFound("Product.NotFound", $"Item with code '{itemCode}' not found.");

        public static readonly Error SapDisabled =
            Error.Failure("Product.SapDisabled", "SAP integration is disabled.");

        public static Error SapTimeout =>
            Error.Failure("Product.SapTimeout", "Connection to SAP Service Layer timed out.");

        public static Error SapConnectionError(string message) =>
            Error.Failure("Product.SapConnectionError", $"Unable to connect to SAP Service Layer. {message}");
    }
}
