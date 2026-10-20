using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class FactoryTransfer
    {
        public static Error LoadFailed(string message) =>
            Error.Failure("FactoryTransfer.LoadFailed", message);

        public static Error ActionFailed(string message) =>
            Error.Failure("FactoryTransfer.ActionFailed", message);
    }
}
