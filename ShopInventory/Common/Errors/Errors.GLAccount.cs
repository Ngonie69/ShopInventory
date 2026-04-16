using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class GLAccount
    {
        public static readonly Error SapDisabled =
            Error.Failure("GLAccount.SapDisabled", "SAP integration is disabled");

        public static Error NotFound(string accountCode) =>
            Error.NotFound("GLAccount.NotFound", $"G/L account with code '{accountCode}' not found");

        public static Error SapError(string message) =>
            Error.Failure("GLAccount.SapError", message);
    }
}
