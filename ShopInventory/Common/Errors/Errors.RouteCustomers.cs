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

        /// <remarks>
        /// An account that sells from a route of its own may only add to that route. Someone who
        /// manages routes rather than selling on one is not scoped this way and may name any.
        /// </remarks>
        public static Error RouteBusinessPartnerNotOwned(string assignedBusinessPartnerCode) =>
            Error.Forbidden(
                "RouteCustomers.RouteBusinessPartnerNotOwned",
                $"This account sells on its own route and cannot add customers to '{assignedBusinessPartnerCode}'.");

        public static readonly Error NameRequired =
            Error.Validation("RouteCustomers.NameRequired", "Customer name is required.");

        public static Error CodeAlreadyExists(string assignedBusinessPartnerCode, string code) =>
            Error.Conflict(
                "RouteCustomers.CodeAlreadyExists",
                $"Route customer code '{code}' already exists for route '{assignedBusinessPartnerCode}'.");

        public static Error CodeNotFoundOnRoute(string code) =>
            Error.NotFound(
                "RouteCustomers.CodeNotFoundOnRoute",
                $"No customer with code '{code}' is on this route.");

        /// <remarks>
        /// Only a route that keeps its own customer list can remove one. An account whose customers
        /// come from head office has nothing local to deactivate, and removing it there would mean
        /// unassigning it from the account — a different act with different consequences, so it is
        /// refused here rather than guessed at.
        /// </remarks>
        public static readonly Error RouteCustomersNotManagedHere =
            Error.Forbidden(
                "RouteCustomers.RouteCustomersNotManagedHere",
                "This account's customers are managed by head office and cannot be removed on the handset.");
    }
}