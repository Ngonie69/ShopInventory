using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class Password
    {
        public static readonly Error InvalidToken =
            Error.Validation("Password.InvalidToken", "Invalid or expired password reset token");

        public static Error ResetFailed(string message) =>
            Error.Failure("Password.ResetFailed", message);

        public static readonly Error Unauthenticated =
            Error.Unauthorized("Password.Unauthenticated", "User is not authenticated");

        public static Error UserNotFound(string userId) =>
            Error.NotFound("Password.UserNotFound", $"User with ID '{userId}' not found");

        public static Error ChangeFailed(string message) =>
            Error.Failure("Password.ChangeFailed", message);

        /// <summary>
        /// The password breaks the policy. One sentence per rule, in one description.
        /// </summary>
        /// <remarks>
        /// A Failure and not a Validation error on purpose. Both are a 400, but a validation problem
        /// carries its sentences in the <c>errors</c> dictionary and boilerplate in <c>detail</c>,
        /// and the till and the handsets each read problem details with their own hand-written
        /// code. <c>detail</c> is the one member all of them show, so that is where this goes.
        /// </remarks>
        public static Error PolicyNotMet(IReadOnlyList<string> broken) =>
            Error.Failure("Password.PolicyNotMet", string.Join(" ", broken));

        public static Error CredentialsNotFound =
            Error.NotFound("Password.CredentialsNotFound", "Credentials not found");

        public static Error UpdateFailed(string message) =>
            Error.Failure("Password.UpdateFailed", message);
    }
}
