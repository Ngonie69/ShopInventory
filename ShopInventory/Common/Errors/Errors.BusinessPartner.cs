using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class BusinessPartner
    {
        public static readonly Error SapDisabled =
            Error.Failure("BusinessPartner.SapDisabled", "SAP integration is disabled");

        public static Error NotFound(string cardCode) =>
            Error.NotFound("BusinessPartner.NotFound", $"Business partner with code '{cardCode}' not found");

        public static readonly Error SearchTermRequired =
            Error.Validation("BusinessPartner.SearchTermRequired", "Search term is required");

        public static Error SapError(string message) =>
            Error.Failure("BusinessPartner.SapError", message);

        public static Error PaymentTermsNotFound(int groupNumber) =>
            Error.NotFound("BusinessPartner.PaymentTermsNotFound", $"Payment terms with group number {groupNumber} not found");
    }
}
