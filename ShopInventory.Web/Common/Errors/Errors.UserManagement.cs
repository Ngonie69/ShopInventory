using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class UserManagement
    {
        public static Error CreateMerchandiserAccountFailed(string message) =>
            Error.Failure("UserManagement.CreateMerchandiserAccountFailed", message);

        public static Error GetManagedMerchandiserAccountsFailed(string message) =>
            Error.Failure("UserManagement.GetManagedMerchandiserAccountsFailed", message);

        public static Error UpdateMerchandiserAssignedCustomersFailed(string message) =>
            Error.Failure("UserManagement.UpdateMerchandiserAssignedCustomersFailed", message);
    }
}