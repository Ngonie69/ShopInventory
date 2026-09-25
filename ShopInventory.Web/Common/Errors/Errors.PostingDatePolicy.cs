using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class PostingDatePolicy
    {
        public static Error Failed(string message) =>
            Error.Failure("PostingDatePolicy.Failed", message);
    }
}
