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

        public static readonly Error SalesRepCanOnlyCreateMerchandisers =
            Error.Unauthorized(
                "UserManagement.SalesRepCanOnlyCreateMerchandisers",
                "Sales representatives can only create merchandiser accounts.");

        public static readonly Error SalesRepCannotAssignCustomPermissions =
            Error.Unauthorized(
                "UserManagement.SalesRepCannotAssignCustomPermissions",
                "Sales representatives cannot assign custom permissions when creating merchandiser accounts.");

        public static readonly Error PodOperatorCanOnlyCreateDrivers =
            Error.Unauthorized(
                "UserManagement.PodOperatorCanOnlyCreateDrivers",
                "POD operators can only create driver accounts.");

        public static readonly Error PodOperatorCannotAssignCustomPermissions =
            Error.Unauthorized(
                "UserManagement.PodOperatorCannotAssignCustomPermissions",
                "POD operators cannot assign custom permissions when creating driver accounts.");

        public static readonly Error PodOperatorCanOnlyManageDrivers =
            Error.Unauthorized(
                "UserManagement.PodOperatorCanOnlyManageDrivers",
                "POD operators can only manage driver accounts.");

        public static readonly Error PodOperatorCannotAssignCustomPermissionsOnUpdate =
            Error.Unauthorized(
                "UserManagement.PodOperatorCannotAssignCustomPermissionsOnUpdate",
                "POD operators cannot assign custom permissions when updating driver accounts.");

        public static Error UpdateFailed(string message) =>
            Error.Failure("UserManagement.UpdateFailed", message);

        public static readonly Error Unauthenticated =
            Error.Unauthorized("UserManagement.Unauthenticated", "User is not authenticated");
    }
}
