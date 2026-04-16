using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class UserManagement
    {
        public static Error NotFound(Guid id) =>
            Error.NotFound("UserManagement.NotFound", $"User with ID {id} not found");

        public static Error CreationFailed(string message) =>
            Error.Failure("UserManagement.CreationFailed", message);

        public static Error UpdateFailed(string message) =>
            Error.Failure("UserManagement.UpdateFailed", message);

        public static readonly Error Unauthenticated =
            Error.Unauthorized("UserManagement.Unauthenticated", "User is not authenticated");
    }
}
