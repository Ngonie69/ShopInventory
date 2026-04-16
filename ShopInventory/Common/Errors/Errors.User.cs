using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class User
    {
        public static Error NotFound(Guid id) =>
            Error.NotFound("User.NotFound", $"User with ID {id} not found");

        public static readonly Error DuplicateUsername =
            Error.Conflict("User.DuplicateUsername", "Username already exists");

        public static Error CreationFailed(string message) =>
            Error.Failure("User.CreationFailed", message);

        public static Error UpdateFailed(string message) =>
            Error.Failure("User.UpdateFailed", message);

        public static Error DeleteFailed(string message) =>
            Error.Failure("User.DeleteFailed", message);

        public static readonly Error LastAdmin =
            Error.Validation("User.LastAdmin", "Cannot delete the last admin user");
    }
}
