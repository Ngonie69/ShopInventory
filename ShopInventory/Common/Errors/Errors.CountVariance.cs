using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class CountVariance
    {
        public static readonly Error SapDisabled =
            Error.Failure("CountVariance.SapDisabled", "SAP integration is disabled, so no inventory count can be read.");

        public static Error NotFound(int documentEntry) =>
            Error.NotFound("CountVariance.NotFound", $"SAP has no inventory count with entry {documentEntry}.");

        public static Error SapReadFailed(string what, string reason) =>
            Error.Failure("CountVariance.SapReadFailed", $"SAP could not {what}: {reason}");
    }
}
