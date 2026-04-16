using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class Merchandiser
    {
        public static Error NotFound(Guid userId) =>
            Error.NotFound("Merchandiser.NotFound", $"Merchandiser with user ID {userId} not found");

        public static readonly Error Unauthenticated =
            Error.Unauthorized("Merchandiser.Unauthenticated", "User is not authenticated");

        public static Error SapError(string message) =>
            Error.Failure("Merchandiser.SapError", message);

        public static Error AssignmentFailed(string message) =>
            Error.Failure("Merchandiser.AssignmentFailed", message);
    }
}
