using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class ExchangeRate
    {
        public static Error NotFound(string from, string to) =>
            Error.NotFound("ExchangeRate.NotFound", $"Exchange rate not found for {from}/{to}");

        public static Error ConversionFailed(string message) =>
            Error.Failure("ExchangeRate.ConversionFailed", message);

        public static Error FetchFailed(string message) =>
            Error.Failure("ExchangeRate.FetchFailed", message);
    }
}
