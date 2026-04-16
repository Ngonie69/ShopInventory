using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class UserActivity
    {
        public static readonly Error Unauthenticated =
            Error.Unauthorized("UserActivity.Unauthenticated", "User is not authenticated");

        public static Error FetchFailed(string message) =>
            Error.Failure("UserActivity.FetchFailed", message);
    }
}
