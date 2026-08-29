using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class VanSalesCustomerAccount
    {
        public static Error LoadFailed(string message) =>
            Error.Failure("VanSalesCustomerAccount.LoadFailed", message);

        public static Error OnboardFailed(string message) =>
            Error.Failure("VanSalesCustomerAccount.OnboardFailed", message);

        public static Error DeactivateFailed(string message) =>
            Error.Failure("VanSalesCustomerAccount.DeactivateFailed", message);
    }
}
