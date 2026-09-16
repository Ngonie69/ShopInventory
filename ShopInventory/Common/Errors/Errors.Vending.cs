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

        /// <remarks>
        /// A till adds vendors only where it invoices them. A shop till sells to walk-ins and keeps no
        /// vendor list, so anything it added would be a customer nothing sells to.
        /// </remarks>
        public static readonly Error AccountDoesNotKeepVendors =
            Error.Forbidden(
                "Vending.AccountDoesNotKeepVendors",
                "This till does not sell to vendors, so it cannot add one.");

        /// <remarks>
        /// The account sells on a business partner no vending account is recognised on, so there is no
        /// depot prefix to number a vendor with. Adding one anyway would give it a code outside the
        /// convention.
        /// </remarks>
        public static Error NotAVendingDepot(string businessPartnerCode) =>
            Error.Validation(
                "Vending.NotAVendingDepot",
                $"{businessPartnerCode} is not set up as a vending depot, so vendors cannot be numbered here. Ask an administrator to add the vendor on the Vending page.");

        /// <remarks>
        /// The same name and phone already on the depot's list. A generated code is new on every request,
        /// so without this a retried add lists one person twice and splits their takings.
        /// </remarks>
        public static Error VendorAlreadyListed(string code, string name) =>
            Error.Conflict(
                "Vending.VendorAlreadyListed",
                $"{name} is already a vendor here, as {code}. Choose them from the list instead.");

        public static Error FieldTooLong(string label, int maxLength) =>
            Error.Validation(
                "Vending.FieldTooLong",
                $"{label} is longer than {maxLength} characters.");

        private static string VendorCodeExample(string prefix) => $"{prefix}001";
    }
}
