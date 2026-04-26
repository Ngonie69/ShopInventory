using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class UserManagement
    {
        public static Error CreateMerchandiserAccountFailed(string message) =>
            Error.Failure("UserManagement.CreateMerchandiserAccountFailed", message);
    }
}