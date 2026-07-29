using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class RouteCustomers
    {
        public static Error NotFound(int routeCustomerId) =>
            Error.NotFound("RouteCustomers.NotFound", $"Route customer '{routeCustomerId}' was not found.");

        public static readonly Error UserNotFound =
            Error.NotFound("RouteCustomers.UserNotFound", "User was not found.");

        public static readonly Error UserInactive =
            Error.Unauthorized("RouteCustomers.UserInactive", "User is not active.");

        public static readonly Error RouteBusinessPartnerRequired =
            Error.Validation("RouteCustomers.RouteBusinessPartnerRequired", "An assigned business partner code is required for route customers.");

        public static readonly Error NameRequired =
            Error.Validation("RouteCustomers.NameRequired", "Customer name is required.");

        public static Error CodeAlreadyExists(string assignedBusinessPartnerCode, string code) =>
            Error.Conflict(
                "RouteCustomers.CodeAlreadyExists",
                $"Route customer code '{code}' already exists for route '{assignedBusinessPartnerCode}'.");
    }
}