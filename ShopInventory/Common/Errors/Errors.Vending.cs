using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class Vending
    {
        /// <remarks>
        /// A vendor's code prefix comes from the depot's warehouse, so a depot with no warehouse, an
        /// unmapped one, or two that disagree cannot say what a new vendor's code should be.
        /// </remarks>
        public static Error DepotCannotNumberVendors(string businessPartnerCode, string problem) =>
            Error.Validation(
                "Vending.DepotCannotNumberVendors",
                $"Vendors cannot be added at {businessPartnerCode} yet: {problem}.");

        public static Error VendorCodeDoesNotFitDepot(string code, string businessPartnerCode, string prefix) =>
            Error.Validation(
                "Vending.VendorCodeDoesNotFitDepot",
                $"Vendor code '{code}' does not fit {businessPartnerCode}: its vendor codes are {prefix} and three digits, like {VendorCodeExample(prefix)}.");

        public static Error VendorCodeTaken(string code, string businessPartnerCode) =>
            Error.Conflict(
                "Vending.VendorCodeTaken",
                $"Vendor code '{code}' is already used at {businessPartnerCode}.");

        public static Error VendorCodesExhausted(string prefix) =>
            Error.Conflict(
                "Vending.VendorCodesExhausted",
                $"Every {prefix} vendor code up to {prefix}999 has been issued.");

        /// <remarks>
        /// Another write took one of the file's codes between the check and the save. The unique
        /// (business partner, code) index refused it, and nothing from the file was saved.
        /// </remarks>
        public static readonly Error ImportCollided =
            Error.Conflict(
                "Vending.ImportCollided",
                "Another change took one of these vendor codes while the file was being imported, so nothing was imported. Upload the file again to re-check it.");

        private static string VendorCodeExample(string prefix) => $"{prefix}001";
    }
}
