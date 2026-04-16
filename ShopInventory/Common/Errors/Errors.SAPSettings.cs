using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class SAPSettings
    {
        public static Error UpdateFailed(string message) =>
            Error.Failure("SAPSettings.UpdateFailed", message);

        public static Error ConnectionTestFailed(string message) =>
            Error.Failure("SAPSettings.ConnectionTestFailed", message);
    }
}
