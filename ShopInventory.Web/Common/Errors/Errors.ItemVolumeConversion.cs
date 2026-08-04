using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class ItemVolumeConversion
    {
        public static Error LoadFailed(string message) =>
            Error.Failure("ItemVolumeConversion.LoadFailed", message);

        public static Error SaveFailed(string message) =>
            Error.Failure("ItemVolumeConversion.SaveFailed", message);

        public static Error DeleteFailed(string message) =>
            Error.Failure("ItemVolumeConversion.DeleteFailed", message);
    }
}
