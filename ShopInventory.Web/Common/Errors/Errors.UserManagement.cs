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

        public static Error GetDriverBusinessPartnerAccessFailed(string message) =>
            Error.Failure("UserManagement.GetDriverBusinessPartnerAccessFailed", message);

        public static Error RefreshDriverBusinessPartnerAccessFailed(string message) =>
            Error.Failure("UserManagement.RefreshDriverBusinessPartnerAccessFailed", message);

        public static Error UpdateDriverBusinessPartnerAccessFailed(string message) =>
            Error.Failure("UserManagement.UpdateDriverBusinessPartnerAccessFailed", message);
    }
}