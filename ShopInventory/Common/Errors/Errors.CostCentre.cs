using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class CostCentre
    {
        public static readonly Error SapDisabled =
            Error.Failure("CostCentre.SapDisabled", "SAP integration is disabled");

        public static readonly Error InvalidDimension =
            Error.Validation("CostCentre.InvalidDimension", "Dimension must be between 1 and 5");

        public static Error NotFound(string centerCode) =>
            Error.NotFound("CostCentre.NotFound", $"Cost centre with code '{centerCode}' not found");

        public static Error SapError(string message) =>
            Error.Failure("CostCentre.SapError", message);
    }
}
