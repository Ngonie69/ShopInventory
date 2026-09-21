using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    /// <summary>What the SAP user administration screen can fail at.</summary>
    public static class SapUserAccount
    {
        public static Error LoadFailed(string message) =>
            Error.Failure("SapUserAccount.LoadFailed", message);

        public static Error UnlockFailed(string message) =>
            Error.Failure("SapUserAccount.UnlockFailed", message);

        public static Error ChangePasswordFailed(string message) =>
            Error.Failure("SapUserAccount.ChangePasswordFailed", message);
    }
}
