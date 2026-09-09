using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class TransferListener
    {
        public static Error LoadFailed(string message) =>
            Error.Failure("TransferListener.LoadFailed", message);

        public static Error CheckFailed(string message) =>
            Error.Failure("TransferListener.CheckFailed", message);
    }
}
