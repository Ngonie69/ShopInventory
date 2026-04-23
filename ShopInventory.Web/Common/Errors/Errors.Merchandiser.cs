using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class Merchandiser
    {
        public static Error LoadProductsFailed(string message) =>
            Error.Failure("Merchandiser.LoadProductsFailed", message);

        public static Error BackfillFailed(string message) =>
            Error.Failure("Merchandiser.BackfillFailed", message);
    }
}