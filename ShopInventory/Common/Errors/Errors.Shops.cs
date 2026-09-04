using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class Shops
    {
        public static Error NotFound(int id) =>
            Error.NotFound("Shops.NotFound", $"Shop with ID {id} was not found");

        public static Error NotFoundByCode(string code) =>
            Error.NotFound("Shops.NotFoundByCode", $"Shop '{code}' was not found");

        public static Error DuplicateCode(string code) =>
            Error.Conflict("Shops.DuplicateCode", $"A shop with code '{code}' already exists");

        /// <summary>
        /// Two shops sharing a warehouse.
        /// </summary>
        /// <remarks>
        /// Refused because the warehouse is what scopes a till operator's view of the day's takings —
        /// see <c>GetDesktopSalesHandler</c>. Two shops on one warehouse would show each other's sales
        /// to both, which is precisely the separation the scope exists to enforce. If the business ever
        /// genuinely needs two shops selling from one warehouse, the scope has to move to the shop
        /// before this rule can be relaxed, not after.
        /// </remarks>
        public static Error WarehouseAlreadyAssigned(string warehouseCode, string shopName) =>
            Error.Conflict("Shops.WarehouseAlreadyAssigned",
                $"Warehouse '{warehouseCode}' is already used by {shopName}. Each shop needs its own warehouse.");

        /// <summary>
        /// Closing a shop that still has people assigned to it.
        /// </summary>
        /// <remarks>
        /// Their accounts would keep authenticating and then fail at the first sale with a refusal
        /// naming the shop, which reads to an operator as a broken till rather than a closed shop.
        /// Reassigning them first is the right prompt.
        /// </remarks>
        public static Error HasAssignedOperators(string shopName, int operatorCount) =>
            Error.Conflict("Shops.HasAssignedOperators",
                $"{shopName} still has {operatorCount} active till operator(s) assigned. Reassign them before closing it.");

        public static Error CodeRequired =>
            Error.Validation("Shops.CodeRequired", "A shop code is required");

        public static Error NameRequired =>
            Error.Validation("Shops.NameRequired", "A shop name is required");

        public static Error BusinessPartnerRequired =>
            Error.Validation("Shops.BusinessPartnerRequired",
                "A business partner is required — it is who the shop's sales are invoiced to");

        public static Error WarehouseRequired =>
            Error.Validation("Shops.WarehouseRequired",
                "A warehouse is required — it is where the shop's stock leaves from");
    }
}
